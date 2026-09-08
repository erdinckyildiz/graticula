# What a Server Adds — the evidence for Q-18

**Status:** EVIDENCE, not an answer. Written 2026-09-09.
**For:** [Q-18](open-questions.md), one of the two questions carried from Phase 0 as
blocking.
**Reads with:** [research/postgis-thin-servers.md](research/postgis-thin-servers.md),
which sharpened the question; [v1-scope.md](v1-scope.md), which is what actually got
built; [MASTER_GIS_PLATFORM_PROMPT.md](../MASTER_GIS_PLATFORM_PROMPT.md) §82, which is
the test each subsystem has to pass.

---

## 1. The question, and what this document is

Q-18 asks:

> What genuinely justifies a server over static cloud-native publishing **and over a
> stateless thin server plus a capable database?**

`research/postgis-thin-servers.md` gave it its sharpest form: pg_tileserv deletes
publishing, authorization, geoprocessing and MVT encoding **with one constraint** — it
speaks to PostGIS and nothing else. *"Each subsystem we keep must be individually
defensible."*

**This document does not answer Q-18, and that is deliberate.** The question is an
argument rather than a measurement, and the part of it that is a product decision —
*is this product worth building* — belongs to the owner. What can be done without
them is to stop the argument being conducted from memory: to say, subsystem by
subsystem and with citations into the code, **what this server does that a thin server
plus PostGIS does not**, and to say plainly where the answer is *nothing much*.

**So the deliverable is a ledger, and it is meant to be read against §82** — *for
every proposed technology, what concrete problem does this solve?* Sections 4 and 6 are
the two halves that matter: what is defended, and what is not.

## 2. Method, and what counts as evidence here

Three rules, because the failure mode of a document like this is a list of adjectives.

1. **A claim about the code cites the code.** Not the design, not the ADR — the file.
   The ADRs have been wrong about the code twice in the last two days
   ([ADR-059](adr/ADR-059-quiescing-a-data-source.md) §5g,
   [ADR-058](adr/ADR-058-the-datastore-schema-is-edited-from-the-screen.md) §5c), both
   times because a sentence was written from the shape of the design.
2. **A claim about behaviour is measured on a running server**, and the numbers are
   here rather than described.
3. **A subsystem with no requirement behind it is named as such.** §82's question has
   an answer of *nothing* for some of this, and a ledger that never says so is
   advocacy.

**What this document deliberately does not do** is benchmark this server against
pg_tileserv. That comparison is worth having and it is not this: the interesting
difference is not requests per second, it is what each one can be asked to do at all.
`benchmarks/mvt-generation/` is where the performance half already lives.

## 3. The census

Three numbers, each read off the built thing rather than the plan.

### 3a. The HTTP surface is mostly administration

**106 distinct literal paths**, carrying **128 method-and-path pairs** (`.MapGet` /
`.MapPost` / … with a literal path) — before the parameterised protocol surfaces that are
registered in loops: the FeatureServer, VectorTileServer, MapServer, WMS, WFS and OGC API
Features paths, which this census does not count at all.

| Prefix | Paths | Method+path |
|---|---|---|
| `/admin` | **79** | **100** |
| `/rest` | 12 | 13 |
| GeometryServer operations | 9 | 9 |
| `/content`, `/console` | 4 | 4 |
| `/healthz`, `/` | 2 | 2 |

**Three quarters of the named surface is the administration API, and by the second count
it is closer to four fifths** — because an administrative resource is the kind of thing
you `PUT` and `DELETE` as well as `GET`, and a protocol surface is not. That is the shape
of `research/postgis-thin-servers.md` §3.3's finding stated as a number: the
thin-server model is *"an architecture with no administrator in it"*, and this one is
mostly an administrator's.

**Read carefully, because the number flatters.** The routes a *client* uses are the
parameterised ones this census does not count, and they carry all the traffic. What
the 79 measure is not importance — it is **surface area that has no counterpart at
all** in a thin server, because there is nothing to administer when a table is a
service.

### 3b. A service is 25 columns, and eight of them are a ceiling

The `service` table, read off a live schema:

| What the columns are for | Columns |
|---|---|
| **Cost ceilings** — `capability_ceiling`, `statement_timeout_ms`, `max_record_count`, `default_record_count`, `max_response_bytes`, `max_request_bytes`, `max_edits_per_transaction`, `request_deadline_seconds` | **8** |
| Identity and addressing — `id`, `name`, `folder`, `kind`, `description` | 5 |
| Lifecycle — `status`, `created_at`, `updated_at` | 3 |
| Appearance — `style`, `style_updated_at` | 2 |
| Which faces are on — `serves_features`, `serves_tiles` | 2 |
| The reference it is served in — `srid`, `srid_wkt` | 2 |
| Ownership and access — `owner_principal_id`, `sharing` | 2 |
| Composition — `next_layer_index` | 1 |

**A third of what a service *is*, is a bound on what one caller may take.** A thin
server's answer to that whole column group is `statement_timeout` in the database and
whatever the reverse proxy in front of it does — which bounds a *statement* and a
*connection*, and not a response's size, a page's record count, an edit transaction's
extent, or how long one client may occupy the service.

The `layer` table is 24 columns, of which **seven address the table** (`schema_name`,
`table_name`, `geometry_column`, `srid`, `identity_column`, `object_id_column`,
`geometry_type`) and **seventeen record a decision somebody made about it** — sharing,
status, cache lifetime, symbology, time field, attachment quota, which service it is
in and at which index, and which group inside that service.

**The seven are exactly what auto-discovery derives.** That is the cleanest statement
of the boundary this document is trying to draw: pg_tileserv computes the seven and has
nowhere to put the seventeen.

### 3c. The platform store is 26 tables

`api_key`, `audit_event`, `client_event`, `coverage`, `data_source`, `folder`,
`group_layer`, `job`, `layer`, `local_credential`, `login_attempt`, `platform_schema`,
`principal`, `principal_role`, `relationship`, `request_log`, `role`, `role_privilege`,
`service`, `session`, `setup_token`, `sharing_group`, `sharing_group_item`,
`sharing_group_member`, `system_service`, `user_type`.

**This is the honest form of the thin-server challenge**: every one of these is state
that exists because this server is not stateless, and each needs a reason.

## 4. Subsystem by subsystem

The five candidate answers `research/postgis-thin-servers.md` §5 named, plus one it
did not.

### 4a. Multi-provider access — the one that pays for everything else

**What the research says**: every deletion pg_tileserv earns depends on there being
exactly one data source that happens to be a capable database. §3.1 states the price
plainly and says the migration goal is what pays for it.

**What is actually built, 2026-09-09**: `LayerDefinition` names a schema, a table and
a geometry column, and `Graticula.Providers.PostGis` is the only provider. **v1 is
PostGIS only** ([v1-scope.md](v1-scope.md) §3a) — a deferral rather than a removal,
with the other databases added afterwards by owner decision.

**So this candidate is mostly unspent, and *mostly* is the accurate word.** The
abstraction that forfeits the thin-server dividend exists — `IFeatureSource`,
`ITileSource`, `ICoverageReader` — and on the **vector** side, which is what Q-18 is
about, the second implementation that would justify it does not. On the raster side it
does: `TiffCoverageReaderFactory` implements `ICoverageReaderFactory` and reads a format
PostGIS does not hold at all, which is a genuine second provider and is why
[ADR-043](adr/ADR-043-imageserver-and-the-raster-face.md) exists. **That does not rescue
the vector case**, because the thin-server dividend is a *vector* dividend: pg_tileserv's
four deletions are all about tables in a database. [ADR-008](adr/ADR-008-query-engine.md)
§4a-i already records the cost of that: `FeatureQuery.Where` carries ready-made SQL
text into the domain model, which §4.1 forbids *precisely so that a non-database
provider stays possible*, and it was recorded rather than repaired on the grounds that
*"the only consumer is hypothetical"*.

**Verdict: not defensible today on its own terms, and it does not have to be.** v1-scope
§3a is explicit that this is deferred, and the ledger entry is that **the largest
single source of complexity in the design is being carried on a promise**. What would
settle it is the second provider, not an argument.

**But there is a second reading, and it is stronger.** Registered data — a customer's
own PostGIS, which this server reads without owning — is already two sources in every
sense that matters operationally, even when both speak the same dialect. They have
different credentials, different privileges, different failure modes, different DBAs
and different maintenance windows. `data_source`, `SourceQuiesce`, `SourceBreaker`,
`ConnectionBudget` and `SourceCertificate` all exist for that plurality rather than for
dialect plurality, and none of them would be deleted by v1 staying PostGIS-only
forever. **A thin server pointed at one database has none of them and needs none.**

### 4b. Cache lifecycle and invalidation — half built, and it is the half the research predicted

**What the research says** (§3.2): Martin and pg_tileserv generate on the fly and give
up cache management; Tegola gives up raw speed for seeding and invalidation. *"We
cannot pick one."*

**What is built**: `ITileCache` has `ReadAsync`, `WriteAsync`, `Purge(layerId)` and a
size measure — **invalidation and a lifetime, no seeding**. `tiles.Purge(layer.Id)` is
called when a layer is unpublished, when a symbology changes, and when a schema change
lands; `cache_seconds` is per layer.

**[ADR-010](adr/ADR-010-caching.md) already knows.** §3 designs seeding — resumable,
cancellable, across 1,000 services — and the ADR's own words are that *"the honest
question this ADR cannot yet answer"* is how long seeding a realistic estate takes,
which is `benchmarks/tile-seeding/` and unrun.

**Verdict: defensible and incomplete, and the incompleteness is recorded.** Invalidation
is the half the thin servers actually lack — pg_tileserv has no cache to invalidate and
Tegola's seeding is manual — and it is the half that is built. Seeding is designed,
unbuilt and unmeasured.

### 4c. Service-level observability and administration — defensible, and the largest thing here

**What the research says** (§3.3): there is no service lifecycle to observe because
there are no services, only tables. *"The DBA administers the database, and the tile
server is a stateless process nobody manages."*

**What is built**: `request_log`, `audit_event`, `client_event`, `job`, `login_attempt`,
`ServerLog`, `/admin/logs`, `/admin/health`, `/admin/jobs`, `/healthz/ready`,
`QueryTrace`, `SourceBreaker`, `ConnectionBudget`, and the per-service status
(`started` / `stopped`) that makes *running and refusing* a distinct state from
*stopped* ([ADR-031](adr/ADR-031-service-capability-configuration.md) §2a).

**The four questions §3.3 says a thin server cannot answer**, checked against the
routes that answer them:

| Question | Answered by |
|---|---|
| *why is this service slow* | `/admin/logs`, `QueryTrace`, `statement_timeout_ms` per service |
| *who may see this layer* | `sharing`, `sharing_group*`, `role_privilege`, `/admin/services/{n}/sharing` |
| *when was this cache invalidated* | `cache_seconds`, `Purge`; **and the answer is partial** — see below |
| *which services broke when that column was dropped* | `POST /admin/layers/{name}/refresh`, `ServiceContexts`' 30-second re-read, `CatalogFallback` |

**Verdict: defensible, and it is the answer with the most code behind it.** The primary
user is the GIS administrator ([product-context.md](product-context.md)), and this is
the subsystem that user exists for.

**One gap is worth naming rather than glossing, and it is narrower than I first wrote
it.** There are six purge sites. Exactly one of them —
`POST /admin/layers/{name}/refresh` — records what it did, auditing `layer.refresh` with
the number of tiles it threw away. The other five happen **inside** operations that are
themselves audited (unpublishing a layer, replacing a composition, deleting a service, a
field added or dropped) whose audit rows do not mention the cache at all. So *when was
this cache invalidated* is answered by knowing which operations purge and reading the
audit for those — which is inference from a record rather than a record. Smaller than
*no trace*, and still the weakest of the four.

### 4d. Managed publishing with validation and rollback — defensible, with a measured cost

**What is built**: `POST /admin/publish` composes a service out of tables in registered
databases in one transaction ([ADR-057](adr/ADR-057-composing-and-publishing-a-service.md));
`GET /admin/publish/name` answers whether a name is free before anything is written;
`POST /admin/publish/preview` draws the composition; the probe lists what a credential
may publish; geometry is validated at import and reported.

**What pg_tileserv does instead**: nothing, and that is the feature — *"just point it
at a PostgreSQL/PostGIS database."*

**Verdict: defensible, and the requirement is recorded** — 100–1,000 services with a
named administrator, and a migration story where the data already exists elsewhere. But
the research's §4.1 recommendation was stronger than what got built: it asks for
**auto-discovery as a first-class publishing mode**, *"point it at a schema and publish
what you can read"*, and calls it *"the strongest single idea here"* and a design
requirement.

**What exists is discovery feeding a manual composition.** `PostgresDataSourceProbe`
lists every publishable relation a credential can read, and an operator then composes a
service from them on a screen. That is the wizard §4.1 explicitly said this should
*not* merely be. **Whether the gap matters is a product question** and it is not
recorded anywhere as a decision — which makes it the clearest live §82 item in this
document.

### 4e. A compatibility surface for migration — defensible, and it is the one thing no thin server can do

This is the candidate with the least argument in it, so it gets the measurement instead.

**A FeatureServer layer document has 32 top-level keys** (34 since `parentLayerId` and
`subLayerIds` were added on 2026-09-08). Fetched from a running server and classified by
where each value comes from:

| Where the value comes from | Keys |
|---|---|
| **Protocol constants** — `currentVersion`, `type`, `hasStaticData`, `isDataVersioned`, the seven `supports*` flags, `advancedQueryCapabilities`, `supportedQueryFormats`, `supportedSpatialRelationships`, `supportsCoordinatesQuantization`, and three that are always empty (`description`, `copyrightText`, `globalIdField`) | **17** |
| **Derivable from PostgreSQL's own catalogue** — `geometryType`, `hasZ`, `hasM`, and the names and types inside `fields` | 4 |
| **State this server holds** — `id`, `name`, `objectIdField`, `displayField`, `extent`, `capabilities`, `drawingInfo`, `drawingInfoGenerated`, `maxRecordCount`, `hasAttachments`, `relationships` | **11** |

**The honest reading of that table is that most of the document is constants**, and a
thin server could emit them by writing them down. The 11 are the ones it could not:
`id` and `name` need a service that is not a table; `objectIdField` is *declared* and
never inferred ([Q-57](open-questions.md)); `capabilities` is the caller's privileges
intersected with the service's ceiling; `drawingInfo` is a stored symbology document;
`extent` is in the reference **the service** is served in, which need not be the one
the table is stored in.

**But the number is not the argument.** The argument is that pg_tileserv would have to
choose to speak ArcGIS at all, and the moment it does, it is not a thin server — it has
acquired a service model, a privilege model and a symbology store, which are four of
the subsystems its constraint deleted. **The compatibility surface is the requirement
that makes the rest non-optional**, and it is the one that is actually in v1-scope §1's
sentence.

**Verdict: defensible, and it is the load-bearing one.** With the caveat that it is
load-bearing *for a migration*, and the migration goal has never been validated with a
real GIS team — Q-49's *test with real GIS teams* was **dissolved** rather than
answered ([CLAUDE.md](../CLAUDE.md) §1), on the grounds that a gift owes no market case.

### 4f. Editing, which the research never listed

`applyEdits`, attachments, related records, and the OGC API Features write half are in
v1 and are not in any of the five candidates, because **pg_tileserv and pg_featureserv
are read surfaces**. pg_featureserv serves features; it does not accept them.

A thin server's answer to *let this GIS client edit this table* is *give the client a
database credential*, which is a different product with a different threat model.
`FeatureEdits`, `IFeatureVersions`, `IAttachmentStore`, `max_edits_per_transaction` and
the one-writer-for-both-faces rule (v1-scope §2's *"every edit goes through the writer
ArcGIS `applyEdits` uses"*) exist because the client is a browser or ArcGIS Pro rather
than a psql session.

**Verdict: defensible and underargued.** It belongs in Q-18's list and was not in it.

## 5. Where the thin-server model already won

The ledger is not one-directional, and these are the entries that matter most for §82,
because each is a subsystem this project **did not build**.

- **`ST_AsMVT` is the encoding path**, not our own encoder —
  [ADR-021](adr/ADR-021-tile-encoding.md), after four benchmark rounds
  (`benchmarks/mvt-generation/`). That is research §4.4 taken, **and taken further than it
  asked**: §4.4 wanted `ST_AsMVT` as the default *"with our own encoder as the fallback for
  providers that cannot"*, and **there is no encoder in `src/` at all** — no class matching
  `MvtEncoder`, `VectorTileEncoder` or `EncodeTile`. v1 has no provider that cannot, so the
  fallback would be code written for a caller that does not exist, which is §82's question
  answered in the only honest direction. It comes back with the second provider, and the
  measured design for it is in the experiment rather than in the product — the rule
  [CLAUDE.md](../CLAUDE.md) §1 states as *an experiment becomes a specification with a measured
  target attached*.
- **Arbitrary SQL is publishable, through the database's own view mechanism.**
  Research §4.3 asks for *function layers*. None were built — and one is not needed for
  the unparameterised case, because `PostgresDataSourceProbe` accepts `relkind in ('r',
  'v', 'm', 'f', 'p')`. **Measured 2026-09-08**, and the limits are exact:

  | Relation | Published? | Object id? | Why |
  |---|---|---|---|
  | table | yes | yes | — |
  | plain view (`select *`) | yes | **no** | typmod survives; a view cannot be indexed |
  | view with an explicit `::geometry(Polygon,4326)` cast | yes | **no** | the cast restores the typmod |
  | view over `ST_Centroid(geom)` | **no** | — | typmod lost, and the probe requires `postgis_typmod_srid(…) > 0` |
  | materialized view with a unique `int` index | yes | **yes** | fully servable |

  **So a materialized view with a unique integer index is a function layer**, at the
  cost of being refreshed rather than live. That is a real capability nobody designed,
  and it should be either documented or refused deliberately — see §6b.
- **Row-level security is the authorization model for delegated layers** — research
  §4.2, *"two authorization systems disagreeing is a security defect waiting to
  happen."* Not built. [A-036](architecture-assumptions.md) is `UNVALIDATED` and no
  source file mentions row-level security. **Named here as an unpaid entry rather than
  a win**, because it is the one recommendation that would *remove* code.
- **Dynamic-first raster** — research §4.5. [ADR-043](adr/ADR-043-imageserver-and-the-raster-face.md)
  and [ADR-009](adr/ADR-009-raster-engine.md) carry it.

## 6. Where a subsystem is not defended

Two entries, both found while assembling this document, both measured.

### 6a. Nothing checks whether a published relation can actually be written

`PrivilegedCapabilities` (`Program.cs`) builds the capabilities string from the
**caller's privileges** intersected with the service's ceiling. Nothing anywhere asks
PostgreSQL whether the relation accepts writes.

**Measured end-to-end, 2026-09-08, on a fixture:**

- A materialized view with a unique integer index publishes: `POST /admin/layers` →
  **201**, `arcGisServable: true`.
- Its layer document advertises **`capabilities: Query,Create,Update,Delete`**.
- PostgreSQL refuses every write to it: `insert` → `ERROR 42809: cannot change
  materialized view`.
- The probe reports it as **`writable: true`**, and the console prints *Writable: yes* —
  because the probe asks `has_table_privilege(oid, 'INSERT, UPDATE, DELETE')`, which
  answers about the *grant* and not about the relation. Measured across four kinds:

  | Relation | probe's `writable` | real | `pg_column_is_updatable(geom)` |
  |---|---|---|---|
  | table | yes | **yes** | **t** |
  | plain view | yes | **yes** | **t** |
  | view over `ST_Centroid` | yes | **no** (`0A000`) | **f** |
  | view with `GROUP BY` | yes | **no** (`55000`) | **f** |
  | materialized view | yes | **no** (`42809`) | **f** |

- And the failure surfaces badly. None of `42809`, `55000` or `0A000` has an arm in
  `ErrorResponse`, so all three fall to the `NpgsqlException` arm: **503, "A database
  this server depends on is unreachable… Retry in a few seconds."** For a database that
  is perfectly healthy and a condition that is permanent — beside a comment in the same
  file reading *"telling a client to retry a permanent fault is worse advice than
  none."*

**Why it belongs in a Q-18 document**: this is what *delegating to the database*
looks like when it is done half way. The probe asks the database a question, gets an
answer about privileges, and reports it as an answer about capability.
**`pg_column_is_updatable(oid, attnum, true)` gets all five cases right** and is one
predicate. Recorded as [D-231](architecture-debt.md); not repaired here, because
whether a non-updatable relation should be refused at publish, published read-only, or
published with a warning is a product decision.

### 6b. A materialized view publishes with no fields at all

Same measurement, and it is the sharper half. **`information_schema.columns` does not
list materialized views** — PostgreSQL excludes them, because they are not in the SQL
standard. `PostGisFeatureSource.ReadFieldsAsync` reads `information_schema.columns`;
`PostgresDataSourceProbe` reads `pg_class`. **The two catalogues disagree, and nothing
notices.**

Measured: `q18view.parcel_summary`, a materialized view over a `GROUP BY`, published
successfully, and its layer document came back with **`fields: []`** while its `query`
returned features carrying only `id`. Every attribute is invisible; `applyEdits`
answers *'owner' is not a column of this layer* about a column that is one.

**One line fixes the reading half** — the field query would have to come off `pg_attribute`
rather than `information_schema` — and it is not obviously the right fix, because
`information_schema` is *privilege-filtered* and that is deliberate: `ReadFieldsAsync`'s
own comment says it *"shows what this credential may actually see, not what exists.
That is the honest answer for a capability report."* `pg_attribute` is not filtered, so
the naive repair would report columns the credential cannot read. Also [D-231](architecture-debt.md).

## 7. What is left, and it is the owner's

**What this document establishes** is that of the five candidate answers, three are
defended by code and a recorded requirement (observability and administration, managed
publishing, the compatibility surface), one is defended and half-built (cache
lifecycle), and one is **carried on a deferral** (multi-provider). A sixth — editing —
belongs on the list and was not on it. Two subsystems are named as undefended in §6, and
neither is a reason to build a different product; they are ordinary defects that this
question found.

**What it cannot establish** is the thing Q-18 actually asks, and there are two halves
to that:

1. **Is the migration requirement real?** Everything in §4e rests on somebody wanting
   to move off ArcGIS Server onto something that speaks the same protocol. That was
   never validated, and the validation route was *dissolved* rather than answered.
   Q-18 cannot close while its load-bearing requirement is an assumption.
2. **Is auto-discovery owed?** §4d is the one place where the research made a specific
   design demand — *a genuine mode, not merely a bulk import wizard* — and a wizard is
   what exists. That is a decision nobody has taken. It is now
   [Q-150](open-questions.md).

**The recommendation this document does make** is about the *form* of Q-18's answer
rather than its content: it should be answered per subsystem, in this ledger, with each
entry either defended or removed — and not as a single yes. A question that has been
open since Phase 0 because it is too large to answer at once is a question that should
be cut into pieces that can be.
