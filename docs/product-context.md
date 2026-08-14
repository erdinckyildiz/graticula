# Product Context

**Status:** ANSWERED — the blocking owner questions are closed. Remaining `TBD`
items are secondary.

---

## Why this document exists

The master prompt (§82) requires every proposed technology to answer *"what
concrete problem does this solve?"* — but never asks that question of the
product itself. An architecture cannot be evaluated against an unstated need:
"is this too complex?" is unanswerable without knowing who operates it and what
they are trying to do.

This document holds the answer. It is an input to every ADR and the yardstick
for every review gate.


## What this product is, in one sentence

**gis-server is ArcGIS Portal, ArcGIS Server and ArcGIS Data Store fused into a
single deployable**, decided in [ADR-019](adr/ADR-019-portal-server-split.md).

Calling it *a GIS server* understated it and made several decisions look
arbitrary that are in fact structural: the datastore is mandatory (Q-69, Q-70)
because the Data Store tier is fused in; items, owners, sharing, roles and user
types exist (ADR-018) because the Portal tier is fused in; and the admin API
manages members and certificates as well as layers because it is both admin
surfaces at once.

**The baseline deployment is still one process against one PostgreSQL**
(CLAUDE.md §6). Fusion is what makes that possible — and what it spends is
isolation, which ADR-019 §4 keeps as an internal seam and ADR-019 §7 records as
not yet true.

## Decisions taken by the project owner

> **Read [v1-scope.md](v1-scope.md) first.** It is the authoritative statement of v1 and
> **the first decision in this project that removed scope rather than adding
> it.** Where a row below disagrees with it, v1-scope wins.

| Topic | Decision | Consequence |
|---|---|---|
| **v1 scope (Q-88)** | **PostGIS only — hosted and registered — with ArcGIS FeatureServer, VectorTileServer and GeometryServer. OGC API Features moves to v2.** | Removes five databases, rendering, user-supplied Python and most of the protocol surface. Inverts ADR-005, which is `REOPENED`. See [v1-scope.md](v1-scope.md) |
| **Why it exists (Q-49)** | **"I will give this to the world."** Open source, public, unrestricted. Sufficient on its own; a gift owes no market case. Dissolves §81's requirement to test Q-49 with real GIS teams. | Answered 2026-08-13 — see [competitive-position.md](competitive-position.md) §6 |
| **Positioning** | **Not** "better capabilities than GeoServer" — measurably false and getting more so. **The ArcGIS Server exit path**: FeatureServer compatibility with edits, free migration tooling, a real service runtime, never-degrade-silently. | Sharpened 2026-08-13 |
| **Licence** | **Apache-2.0** (Q-73). Permissive with an express patent grant. Anyone may fork, close and sell it — accepted, because that is what *give it to the world* means. **Consequence: GPL and AGPL dependencies are now disqualified**, since we cannot sublicense them under Apache-2.0. The earlier *copyleft acceptable* note was about inbound dependencies and is superseded by this. | No dependency is excluded on licence grounds. LGPL/MIT are free of friction. AGPL obligations apply over the network for a server product and must be stated explicitly wherever relevant. |
| Scale target | 100–1,000 published services | Kubernetes, mandatory Redis, message brokers and container-per-service are out of scope for the baseline. Worker pooling and DB connection budgeting are in scope and serious: a naive process-per-service model does not survive 1,000 services. |
| Core language | **C# / .NET** | Decided 2026-08-12. [ADR-001](adr/ADR-001-core-language.md) §6 records that the two-language comparison was deliberately not run, and why. Effort moved to absolute measurement, since A-019 gates the architecture and the language choice does not. |
| Build vs adopt | Own the server domain; adopt foundational libraries behind our own ports; never adopt finished GIS server products | See [build-vs-adopt-policy.md](build-vs-adopt-policy.md) |
| **Primary user** | **The GIS administrator** | Answered 2026-08-12 — see below |
| **Day-one workload** | **Features first, then vector tiles** | Answered 2026-08-12 — see below |
| **Migration posture** | **Displacing existing ArcGIS Server / GeoServer deployments is a goal** | Answered 2026-08-12 — see below |
| **Rendering** | ~~Vector-first. No server-side raster tiles. Raster imagery catalogued, not rasterised.~~ **`SUPERSEDED BY ACCUMULATION` 2026-08-13.** Replacement posture: **the client renders by default; the server renders where a protocol requires it.** | Decided 2026-08-12, dismantled 2026-08-13 by Q-17c, Q-78 and Q-83 without ever being reversed — see [reviews/contradiction-sweep-1.md](reviews/contradiction-sweep-1.md) S1 |
| **Databases** | ~~PostgreSQL is not mandatory~~ → **PostgreSQL IS mandatory** (Q-70). The datastore is required (Q-69) and PostGIS-only (Q-32). Oracle Spatial and SQL Server Spatial remain first-class **providers** — registered sources with full read/write — but are not platform stores, not datastores and not tile sources. | Reversed 2026-08-12, same day as the original decision — see below |
| **Storage model** | **Hosted data in a managed datastore; referenced data in registered PostGIS / Oracle / SQL Server sources.** | Answered 2026-08-12 — see [data-model.md](data-model.md) |
| **Native API** | **OGC API Features, Parts 1 + 2 + 3. WFS, WMS and WMTS move to the compatibility layer.** | Answered 2026-08-12 — see [ADR-005](adr/ADR-005-api-architecture.md) |
| **Editing** | **In scope.** Through our API *and* directly against the database with QGIS. Both paths coexist. | Answered 2026-08-12 |
| **Provider write capability** | **Full read/write on all three spatial engines** (Q-50a). Not read-only, not migrate-then-serve. Registered Oracle and SQL Server layers are editable through our API, subject to granted rights. | Answered 2026-08-12 |
| **Migration tooling** | **Inventory plus definition import, free.** Scan the source server and report honestly what can and cannot come across, then import definitions. Data stays in place. | Answered 2026-08-12 |
| **Datastore** | **v1, PostGIS only, MANDATORY (Q-69), shipped as a managed appliance** we install and operate (Q-32). Not three engines - that was an over-application of the no-mandatory-PostgreSQL decision. | Answered 2026-08-12 |
| **Data ownership** | **No default.** Hosted and registered are both first class, chosen per layer by a lifecycle test: does the data have a life outside the service? | Answered 2026-08-12 |
| **Compatibility surface** | ~~WFS, WMTS-for-vector-tiles, full FeatureServer; not GeometryServer or GPServer~~ → **superseded by Q-88.** v1 is **ArcGIS FeatureServer, VectorTileServer and GeometryServer**, and they are the *primary* surface rather than a compatibility layer. WFS and WMTS move to v2 with OGC API Features. | Answered 2026-08-12, superseded 2026-08-13 — [v1-scope.md](v1-scope.md) |
| **Vector tile sources** | **Hosted data only, strictly** (Q-67). Registered Oracle, SQL Server and foreign PostGIS layers serve features and never tiles. | Answered 2026-08-12, against measured evidence rather than in advance of it — see below |

## Two users, two publishing paths

**Decided 2026-08-12.** The GIS administrator is the primary user, but not the
only one, and the second changes the shape of publishing.

| | Publisher | GIS administrator |
|---|---|---|
| Publishes | Their own hosted data | Services from registered sources |
| Surface | Web and API, **self-service** | **Desktop - the QGIS extension** |
| Owns the result | **Yes** | No, it is a corporate service |
| Needs an administrator | No | They are one |

This resolves an ambiguity. The QGIS extension was given a "publishing" role
without noticing that publishing is two different acts. The extension serves the
**administrative** path - publishing services from registered sources.
Self-service publishing is a separate surface and does not involve QGIS.

> **Note added 2026-08-12:** self-service publishing was briefly treated as our
> differentiator. **GeoNode already provides it** — user upload, item ownership,
> groups and sharing — so it is table stakes for a platform of this kind rather
> than a distinguishing capability. It is still worth building; it is not a
> reason to exist. See [competitive-position.md](competitive-position.md).

**It also explains why the datastore must exist.** A publisher cannot be allowed
to write into the corporate enterprise geodatabase, so they need somewhere else
to land. The datastore is not primarily "for organisations without a database" -
it is **where self-service content lives**.

It fits the Q-08 lifecycle test exactly: self-service content is the canonical
case of *the service essentially is the data*, so hosting it and deleting it
with the service is correct.

### What this requires that we had not designed

- **Item ownership.** A hosted item belongs to its creator. They share it, they
  delete it. An administrator can override but need not be involved.
- **Sharing scopes, distinct from roles** - private, group, organisation,
  public. Composes *with* RBAC rather than replacing it.
- **Capability by role**, above permission by role. "May publish hosted content"
  and "may register a data source" are different capabilities, and a publisher
  has the first without the second.
- **Per-user datastore quotas.** Without them one publisher fills the store.
  This makes the L3 size budget a policy question rather than a disk question.
- **A user-facing publishing surface**, not only the admin API (§39).

Open: the exact role set, whether groups are needed in v1, and how quotas are
enforced. See Q-59 to Q-61.

## The primary user is the GIS administrator

Not a developer self-hosting one application, and not an end user. Someone who
administers a GIS estate on behalf of an organisation.

This is the single most consequential input to the architecture, and it moves
several things from "nice" to "required":

- **The administrative API is a first-class deliverable, not a late phase.**
  Everything the UI can do must be automatable (§39). An administrator's job is
  operating many services, and operating many of anything means scripting it.
- **Service lifecycle must be observable** (§23). "Why is this service
  degraded?" must be answerable from the platform, not from log archaeology.
- **The 2 AM test (§7) becomes the primary operational gate**, not a slogan.
  Diagnosability outranks elegance wherever they conflict.
- **Defaults must be good, because per-service tuning will not happen.** At
  1,000 services no administrator hand-tunes worker counts. This raises the
  stakes on assumption A-008 considerably.
- **Multi-user, role-based access is inherent** (§41, §42), not an add-on. An
  administrator administers *for* other people.
- Publishing is a managed, validated, reversible workflow (§38) — not a file
  drop.

## Day one: features, then vector tiles

Confirms the master prompt's own sequencing (§71–§73) and sets the walking
skeleton target:

```text
PostGIS → provider → query engine → OGC API Features over HTTP
```

Priority consequence: [ADR-008](adr/ADR-008-query-engine.md) (query engine) and
[ADR-002](adr/ADR-002-primary-data-architecture.md) rise; rendering
([ADR-004](adr/ADR-004-rendering-engine.md)) and raster
([ADR-009](adr/ADR-009-raster-engine.md)) can be decided later — but not so late
that the runtime model is fixed without accounting for their worker classes.
Deciding ADR-007 as if only feature workloads existed would be a mistake.

## Tiles come only from hosted data (Q-67)

**Decided 2026-08-12, after the measurement rather than before it.**

Vector tiles are served only from data that lives in the datastore as system of
record. A registered Oracle, SQL Server or foreign PostGIS layer gets feature
services, full ArcGIS FeatureServer compatibility including edits, and no tiles
at all — not a slower tile path, none.

**Why, in numbers.**
[benchmarks/mvt-generation/RESULTS.md](../benchmarks/mvt-generation/RESULTS.md)
run 3 measured both. Serving tiles from a source we must read whole geometries
out of: 28.3 req/s, 20 MB allocated per request, **80.9% of wall-clock spent
suspended for garbage collection while using 18% of the CPU**. Serving them
where the database can clip and encode: 96.3 req/s, 0.1 MB per request, 0.3% GC.
The gap is not tuning.

**What it costs, stated plainly.** An organisation running ArcGIS Server on
Oracle with five hundred layers must copy data into our datastore to get tiles,
not merely point at it. That is the ETL our competitors advertise avoiding, and
it was accepted deliberately. The mitigation is that tiles are the *second*
workload (Q-06b): such an organisation gets its feature services on day one
against data that never moves.

**Precedent, marked `VERIFY`.** This matches ArcGIS rather than diverging from
it: Esri's vector tile layers are published from tile packages or hosted layers,
and there is no dynamic vector tile service over a registered enterprise
geodatabase. GeoServer does serve tiles from any store; we are choosing the
Esri shape.

**What it aligns.** The two user types now map onto the two paths without
overlap. Publishers host their own data and get tiles. GIS administrators
register authoritative sources from the desktop and get features. That was not
the reason for the decision, but it is the reason to be comfortable with it.

**What it opens.** Q-68 — whether our own MVT encoder still has a purpose now
that every tile source is PostGIS, which our own Tier 1 rule says it must.
Q-69 — whether the datastore is still *optional*, given that any deployment
wanting tiles must now run it.

## Migration is a goal — the compatibility layer is a requirement

Displacing existing ArcGIS Server and GeoServer deployments is an explicit
objective. Under §51 this promotes the compatibility layer from *optional
investigation* to *product requirement*, with three consequences:

1. **It stays outside the core domain.** §51 is unambiguous, and this becomes
   more important, not less, now that the layer is required. The core domain
   must not learn the shape of anyone else's API. It is an adapter over the
   protocol-neutral internal interface — the same seam as every other protocol
   in [ADR-005](adr/ADR-005-api-architecture.md).
2. **Clean room applies with full force** (§5). A compatible API surface may be
   built from published, publicly documented protocol behaviour only. No
   proprietary source, no undocumented internals, no reverse engineering of
   protected implementation details. This constraint is not negotiable and needs
   to be restated in the compatibility layer's own design document.
3. **Migration is more than an API.** Displacement means moving service
   definitions, styles, caches and client applications. What is actually in
   scope — API compatibility only, or migration tooling as well — is now the
   open question Q-16, and it is a scoping question rather than a technical one.

Which surface to target is Q-17: an ArcGIS-compatible REST surface, standards
(WMS/WFS/WMTS) for GeoServer displacement, or both. The answer depends on which
deployments are actually being displaced, and it materially changes the work.

## Remaining open items

- `TBD` **Data ownership model.** Does the platform own its data (publish into
  it) or serve data owned by others (register existing PostGIS tables)? Given
  the migration goal, *registering existing data* now looks more likely — an
  organisation displacing GeoServer already has its data somewhere. Still needs
  confirmation; it significantly changes the publishing architecture (§38).
- `TBD` **Product name.** Working title `gis-server`.

## Rendering posture — `SUPERSEDED BY ACCUMULATION`, 2026-08-13

> **Read this before the section below.** Vector-first was never reversed. It was
> **out-voted one capability at a time** and the headline was left standing,
> because no single decision contradicted it outright:
>
> | Decision | What it added | What it removed from vector-first |
> |---|---|---|
> | **Q-17c** | ArcGIS ImageServer | *raster imagery is catalogued, not rasterised* |
> | **Q-78** | OGC API Maps | *the client renders* |
> | **Q-83** | server-side rendering and legend | the remainder |
>
> **Replacement posture: the client renders by default; the server renders where
> a protocol requires it.** ADR-004 and ADR-009 both carry the consequences —
> ADR-009 is `REOPENED`, ADR-004 is `DEFERRED` and **that pair is itself a
> contradiction**, raised as Q-85.
>
> Recorded as supersession rather than rewritten, because the *way* it went is
> the finding: [CLAUDE.md](../CLAUDE.md) §2's `INFERRED` rule guards against an
> inference recorded as fact; this is the mirror image — a fact left standing
> after the reasoning beneath it was removed. See [reviews/contradiction-sweep-1.md](reviews/contradiction-sweep-1.md) S1.

### Original section, retained

## Rendering posture — vector-first, the client renders

**Decided by the project owner, 2026-08-12.**

> Tiles will not be raster on the server side. Vector tiles at most.

With two clarifications, also from the owner:

- **WMS and rendered map images live in the compatibility layer**, not the core.
  Low priority, for migration only, most plausibly derived from our own vector
  tiles rather than from a separate cartographic pipeline.
- **Raster and imagery data is catalogued and access-controlled, not
  rasterised.** COG over HTTP range requests; the client renders the pixels.

### What this removes

This is the largest simplification taken so far, and it removes the least
tractable part of the architecture:

- **Label placement, decluttering and cross-tile label consistency leave the
  server.** MapLibre does label placement client-side. Q-26 is closed rather
  than answered — a stateless per-tile renderer producing labels that collide at
  seams is no longer a problem we have.
- **No rasterisation backend in the core.** Skia, Cairo and friends leave the
  critical path entirely.
- **The map rendering worker class disappears** (§20). Four classes remain:
  feature, vector tile, raster metadata, geoprocessing.
- **Style handling drops from evaluation to storage.** We store and serve style
  documents; we do not interpret them. Serving a JSON document is not a
  subsystem.
- **Raster tile caches and pyramids disappear** from the caching architecture.
- **GDAL leaves the request hot path.** See "the quiet consequence" below — this
  turns out to matter more than the rendering change itself.

### What it adds

Small, and mostly unglamorous, but real and easy to forget:

- **Glyph and sprite serving.** MapLibre clients fetch font PBF ranges and
  sprite sheets over HTTP. That is now our job. It is static asset serving, not
  text shaping — but it must exist, and it must work air-gapped, which makes
  font bundling and licensing a real question (Q-15).
- **Style document management.** Storage, versioning, association with services.
- **Client-side styling shifts the migration burden.** ArcGIS renderer JSON and
  SLD do not become MapLibre styles by themselves. This lands on Q-16.

### The quiet consequence — GDAL leaves the hot path

Serving imagery as COG with range requests, rather than decoding pixels
server-side, removes GDAL from per-request work. Three earlier findings change:

- The **GDAL thread-affinity trap**
  ([research/dependency-thread-safety.md](research/dependency-thread-safety.md)
  §5) largely evaporates. It was the most likely site in the platform for a
  subtle, load-dependent correctness bug, and we are no longer doing the thing
  that causes it.
- **Q-24** (do we require GDAL 3.10 for thread-safe raster reads) becomes
  largely moot.
- **A-007** (crash containment is genuinely required) weakens substantially. The
  strongest case for process isolation was native code decoding untrusted raster
  files. We are not decoding them.

GDAL is still needed — for metadata and validation when registering a COG, and
for file-based vector providers — but not thousands of times per second on the
tile path.

### Migration tooling — inventory first, and free

**Decided 2026-08-12 (Q-16).** Two separable steps, a decomposition observed in
Honua Server:

**1. Inventory.** Scan an existing GeoServer or ArcGIS Server and produce a
report: what exists, what we can bring across, and **what we cannot, with the
reason.** This is deliberately available before anyone commits to anything.

It is cheap to build and it is the same philosophy as
[ADR-008](adr/ADR-008-query-engine.md) §2's *never degrade silently* — state the
limits up front rather than after the customer has committed. An honest "we
cannot bring these eleven layers across, here is why" is worth more to an
administrator than an optimistic import that half works.

**2. Definition import.** Service and layer definitions, field configuration,
extents, cache settings. **Data stays where it is** — Q-50a made registered
Oracle and SQL Server fully capable, so there is nothing to move.

Styles are the ragged edge. SLD and ArcGIS renderer JSON do not map cleanly onto
MapLibre style. We convert what converts and report what does not, rather than
producing something that looks converted and renders wrongly.

**Both are free.** `VERIFY` Honua places service imports behind an Enterprise
entitlement while leaving file import in the community tier — a well-drawn
monetisation boundary that says plainly where they believe the value sits.
Giving away what an open-core competitor charges for is a concrete differentiator
and it feeds Q-49.

### v1 migration reach — stated plainly

**WMS is not supported at v1** (Q-47). Rendering raster map images needs a
rasteriser, and vector-first removed that deliberately; reinstating it for a
compatibility surface would cost either a headless GPU context problem that
collides with air-gapped deployment, or the Tier 1 cartographic work the decision
just deleted.

So the v1 compatibility layer is **WFS**, plus **WMTS where it carries vector
tiles**. What that means for migration:

| Consumer speaks | Can migrate at v1 |
|---|---|
| OGC API Features | Yes, natively |
| Vector tiles / MapLibre | Yes, natively |
| WFS | Yes, via the compatibility layer |
| **WMS** | **No** |
| ArcGIS REST | Depends on Q-17 |

Much of a GeoServer estate is consumed through WMS, so this is a material limit
on who can move to us initially. It is a deliberate trade, not an oversight, and
it must appear in migration documentation rather than being discovered during a
pilot.

A rendered map service — closer to an ArcGIS MapService than to a WMS adapter —
remains a possible future capability. If it is built, it should be built as a
product capability with its own justification, not as a legacy adapter.

**Strengthened 2026-08-13.** This is no longer a neutral note. The owner rejects
WMS on its merits — *"I hate WMS. Super slow. Prefer ArcGIS MapServer
capability"* — and considers symbology an opening rather than a checkbox:
*"we can design a better symbology."* v1 scope is unchanged and ADR-004 stays
`DEFERRED`, confirmed on being asked directly. What changed is that the eventual
shape is now specified: a REST rendered map service with our own symbology
model, not a WMS adapter. See [ADR-004](adr/ADR-004-rendering-engine.md) §0 for
what un-deferring would cost, and
[competitive-position.md](competitive-position.md) §6a for why it is the
capability that would make the GeoServer comparison true.

### The tension the owner accepted

Displacing existing ArcGIS Server and GeoServer deployments is a confirmed goal
(Q-07), and those deployments are consumed overwhelmingly through WMS. Clients
that speak WMS — desktop GIS, older web applications, third-party tools — do not
speak vector tiles.

Keeping WMS in the compatibility layer preserves the migration path while
keeping the core clean. The cost is honest and should stay recorded: **migration
is easiest for organisations that can also move their clients to vector tiles.**
A WMS surface derived from our own vector tiles will not be pixel-identical to
what GeoServer produced, and for some consumers that will matter.

### An inversion worth noting

[research/rendering-engines.md](research/rendering-engines.md) argued against
MapLibre Native on the grounds that it is not a neutral rasteriser — it carries
its own styling model, so adopting it would make someone else's style
specification our cartographic architecture.

**That objection is now inverted.** If we adopt the MapLibre style spec
deliberately as our style format (Q-25, now leaning strongly yes), then for the
narrow job of rendering *our own vector tiles* with *our own MapLibre styles*
into WMS images, MapLibre Native stops being a liability and becomes the
obviously correct tool — precisely because it is not neutral. It already speaks
the language we chose.

## Databases — PostgreSQL is mandatory (reversed, same day)

**This section records a decision and its reversal, both by the project owner on
2026-08-12.** It is written as a reversal rather than rewritten as though the
first decision never happened, because the original reasoning was sound and was
outweighed rather than refuted (CLAUDE.md §2).

### Where it landed — Q-70

**PostgreSQL is a hard dependency of every deployment.** It follows from two
later decisions rather than being chosen directly: the datastore is mandatory
(Q-69) and the datastore is PostGIS-only (Q-32).

**Oracle Spatial and SQL Server Spatial remain first-class providers** —
registered sources, full read/write feature services, complete ArcGIS
FeatureServer compatibility including edits, against data that never moves. They
are not platform stores (Q-51), not datastores (Q-32) and not tile sources
(Q-67). **The multi-dialect problem is now entirely a query and write problem,
not a storage problem.**

What it deletes: the SQLite platform-store dialect, one set of migrations, one
job-claim implementation, half the platform-store CI matrix, and A-018.

What it costs: an organisation with a policy against running PostgreSQL cannot
run this product. That is a lost segment and it is accepted. The mitigation is
that the datastore ships as a managed appliance we install, configure, back up
and upgrade — the ask is *run our container*, not *employ a PostgreSQL DBA*.

What it restores: [CLAUDE.md](../CLAUDE.md) §6 has stated the baseline
deployment target as `gis-server → PostgreSQL/PostGIS` since the project began.
The architecture now agrees with it for the first time.

### The original decision, kept on the record

**Decided by the project owner, 2026-08-12, then reversed the same day.**
Invalidated A-009 and reopened
[ADR-002](adr/ADR-002-primary-data-architecture.md). Full analysis:
[research/multi-database-consequences.md](research/multi-database-consequences.md).

The reasoning is stronger than the earlier baseline assumption gave it credit
for. **ArcGIS Server deployments run heavily on SQL Server and Oracle.**
Displacing them is a confirmed goal. Requiring such an organisation to stand up
PostgreSQL — a database they may have no expertise in, no backup tooling for and
no approval to run — merely to hold our service definitions puts a barrier
directly in front of our stated migration target.

Two distinct consequences, which must not be conflated:

**The platform store becomes portable.** — **`SUPERSEDED` by Q-70; PostgreSQL
only.** `VERIFY` **Narrowed 2026-08-12 (Q-51) to SQLite and PostgreSQL only.** SQLite is the embedded default and already meets
the no-PostgreSQL requirement completely, since platform metadata is a few
thousand rows of non-spatial data. SQL Server and Oracle were cut as platform
stores because the remaining justification was an unevidenced policy requirement
costing four dialect implementations and a four-way CI matrix. **They remain
first-class providers and datastores**, where the requirement is real.

**Three first-class spatial dialects.** — **still true, but its consequence was
reversed by Q-67.** The paragraph below reasoned that because `ST_AsMVT` is
PostGIS-only, in-process MVT encoding was the only path for the other two
engines. Q-67 removed the requirement instead: those engines do not serve tiles
at all. In-process encoding went from *the primary path* to *possibly
unnecessary* (Q-68) in the space of a day, and A-001's promotion below should be
read with that in mind.

**Original text.** This is the larger change, and the
single most consequential fact is that **`ST_AsMVT` exists only in PostGIS.** For
SQL Server and Oracle, in-process MVT encoding is not an alternative — it is the
only path. That promotes the language prototype's third endpoint from a
comparison to the primary path, makes A-001 considerably more likely to be true,
and puts our own hot-path geometry primitives back on the critical path.

It also settles Q-21: the Query AST targets multiple dialects from day one, and
capability negotiation is core rather than a later refinement. An abstraction
exercised by a single implementation is not an abstraction.

## Why a server at all

This question must be answered in writing, because a credible answer to it
already exists in the opposite direction.

Client-side platforms such as **GeoLibre** (Tauri, MapLibre GL JS, DuckDB-WASM
Spatial, deck.gl) run a full analysis stack in the browser against remote
GeoParquet, PMTiles and COG over HTTP range requests — no server, no install, no
data leaving the machine. For a large class of published-data workloads, the
correct modern architecture is a static object store and a capable client. That
is not a hypothetical; it ships.

Under §82 the burden of proof is therefore on us. The current answer — to be
argued properly in the assessment, not asserted here — is that a server earns
its place when:

- **data changes**, so tiles and caches must be invalidated rather than
  regenerated wholesale;
- **access must be controlled** per user, per layer, per feature — a static
  bucket cannot do row-level authorization;
- **many services are managed by someone** on behalf of many consumers, which is
  precisely the primary-user answer above;
- **the query exceeds what a client can pull**, so computation must run next to
  the data;
- **editing is required**, with transactions, concurrency control and audit;
- **the environment is air-gapped or governed**, where public object storage is
  not an option.

Where none of these hold, the honest recommendation is to publish PMTiles and
skip the server. A platform that cannot say this about itself is not being
designed, it is being assumed.

This becomes a standing challenge for the Adversarial Reviewer (§6 Agent 12):

> Which parts of this platform are made unnecessary by publishing the data as
> static cloud-native formats?

Recorded as research topic `client-side-platforms.md` and referenced from
assessment §10.

## Non-goals

Stated so that reviews do not drift into them.

- Not a desktop GIS, not a data editor with a UI, not a spatial ETL suite.
- Not feature parity with any existing product (§1).
- Not a novelty exercise. Optimise for the technically strongest, simplest,
  operationally credible solution (§86).

## The standing test

Every architectural proposal is measured against two questions from the master
prompt:

> Could a GIS administrator realistically diagnose and repair this system at
> 2 AM? (§7 — Platform/Operations Architect)

> If ArcGIS Server, GeoServer, MapServer and QGIS Server had never existed as
> products, but we had every lesson learned from them, how would we design this
> today? (§86)
