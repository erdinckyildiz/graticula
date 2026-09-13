# ADR-066 — GeoParquet layers, read in place by DuckDB

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-13, on the owner's request of the same night — *"artık duckdb ve geoparquet üzerinde çalışabiliriz … Sen o ilişkiyi kurar mısın?"* The request to work on DuckDB and GeoParquet is the owner's; **the shape below — a folder of files served in place, read-only, as layers — is `INFERRED`** and listed in §11 for confirmation |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[v1-scope](../v1-scope.md) §3a deferred DuckDB with every database except PostGIS, and its
2026-08-18 amendment said the multi-engine reasoning *sleeps until the engine that needs it
arrives, and it wakes with it*. The owner asked for it to arrive. What was on the record when they
did:

- **[Q-81](../open-questions.md)** — DuckDB is in scope *not as a registered-source provider but as
  the file-format query engine*, *embedded and read-only*.
- **[Q-52](../open-questions.md)** — GeoParquet is an import source, *converted at registration,
  never served from the file*, and also an output format; *DuckDB as the file-query engine … is what
  lets a file be queried without becoming a provider*. The two halves of that answer pull against
  each other, and this ADR has to take a side.
- **[Q-87](../open-questions.md)** — *where does DuckDB execute?* Recommendation **job worker only**,
  on the premise that *DuckDB's spatial extension bundles GDAL*, so serving files from the request
  path would put GDAL into the serving process through a side door.
- **[D-162](../architecture-debt.md)** — `FeatureQuery.Where` carries PostgreSQL SQL, so a second
  dialect would be handed the first one's text. Its trigger: *when DuckDB is designed as the
  file-format query engine, and not after.*
- **[Q-20](../open-questions.md)** — answered 2026-09-09 as *two* geometry engines reachable in v1,
  each checked against PostGIS.
- **The research note** ([duckdb-geoparquet.md](../research/duckdb-geoparquet.md) §2) put
  *read-only reference layers on object storage* against *DuckDB + GeoParquet* — *no database needed
  at all* — and §6 warned: *expect good scans, worse point lookups.*

If DuckDB has any job in this product, it is reading files that are not in the datastore. GeoParquet
already reaches the datastore without it: `Graticula.Import.Reader` carries GDAL's Parquet driver
and the importer converts through it. So a DuckDB that only converted at registration would do what
GDAL already does, and the question the owner asked would have no answer.

## 2. Alternatives considered

### Alternative A — serve a registered folder's GeoParquet files in place, read-only, through DuckDB in the serving process *(chosen, `INFERRED`)*

**Argument for.** It is the only shape in which DuckDB does something the product cannot already
do: a file of a million features becomes a layer by being placed in a folder, with no import job, no
copy in the datastore and no second place for the data to drift from. It is Q-81's *embedded and
read-only* taken literally, and the research note's reference-layer row. It forces D-162's seam
into existence, which that row said has to happen before anything reads across it. And measured
tonight, DuckDB's **core** — no extension — already types GeoParquet as `GEOMETRY`, compares
boxes with `st_intersects_extent`, returns WKB with `st_aswkb`, and prunes on GDAL's covering
columns; the spatial extension Q-87 was afraid of is never needed, so neither GDAL nor GEOS enters
the process.

**Argument against.** DuckDB is a native library that parses files, in the process that answers
public requests. A crash in it is this server's crash, and a malformed file is a parser's input
(§3). Spatial queries are about twice as slow as PostGIS on the same rows (§4). And the file has
no exact spatial predicate, so the exact test is ours.

### Alternative B — convert at registration: GeoParquet into the datastore, served by PostGIS

**Argument for.** Q-52's and Q-87's recommendation, and ADR-009's model for raster. Every face
already works against PostGIS, every predicate is GEOS, nothing parses a file in the serving
process, and the result is editable.

**Argument against.** It needs no DuckDB — the importer's GDAL already reads Parquet — so it is not
an answer to the request. It duplicates the data, and a reference layer that is replaced monthly
becomes an import job monthly. It remains available and is the recommendation for a layer that must
be edited or is queried spatially at high rates (§6).

### Alternative C — DuckDB in a child process, like the overlay worker

**Argument for.** It removes the crash and the parser from the serving process — the argument
ADR-009 §2.2 and Q-97 made for GDAL and NetTopologySuite.

**Argument against.** Every query would cross a pipe carrying every candidate geometry, which is the
cost the overlay worker pays for one geometry per request and a feature query pays for thousands. It
also answers a risk the deployment boundary answers more cheaply: the files are not a caller's (§3).
It is the revisit trigger rather than the design.

### Alternative D — load DuckDB's spatial extension

**Argument for.** It has exact `ST_Intersects`, `ST_Within`, `ST_DWithin`, `ST_Transform` — the
whole relation set, and the provider would refuse nothing.

**Argument against.** It is downloaded at run time (or bundled by us), and it links GEOS, PROJ and
GDAL into the serving process — exactly Q-87's side door, and a third GEOS and a second PROJ whose
answers would have to be reconciled with PostGIS's (Q-20). The core-only design refuses five
relations instead, and says so.

## 3. Counterarguments to the preferred option

**A file parser in the serving process.** ADR-009 §2.2's rule is about *a file somebody else chose*.
The line this ADR draws: a GeoParquet folder is placed on disk by the operator, under a root the
**deployment** names (`Graticula:GeoParquetRoot`), and never arrives through the API. Registration
refuses a path outside the root, a finished `geoparquet:` locator written by hand, and a folder
described with host fields; the root is checked again on every query, so moving it takes folders out
of service. **That line is the whole of the argument**, and the day a GeoParquet file can be uploaded
through the API it is crossed and Alternative C comes back.

**The sandbox has a gap, measured.** After `allowed_directories` is set to the folder, extension
installing and loading are off, external access is off and the configuration is locked, DuckDB
refuses a read outside the folder, `glob` outside it, `INSTALL`, `LOAD` and any setting change — on
the root connection and on every duplicate. **It does not refuse a write inside the folder**:
`COPY … TO` and `ATTACH` into the allowed directory succeeded. Nothing in this server hands DuckDB a
caller's SQL — the where clause is emitted from a parsed tree and every identifier is one the code
already had — so this is a property of the engine rather than a path to it; the compose file mounts
the folder read-only, and the image creates the root owned by root.

**Q-52 said *never served from the file*.** This reverses that sentence for GeoParquet, and Q-52's
own next sentence — DuckDB *lets a file be queried* — is what it is reversed towards. The reversal is
`INFERRED` from the request, not stated by the owner, and §11 asks.

**Twice as slow on spatial queries.** §4's numbers are a 30,000-polygon file in one row group, where
the covering column prunes nothing and every spatial query reads every box. PostGIS has a GiST index.
A layer where that matters belongs in the datastore, and the ADR does not claim otherwise.

**A third engine evaluating `intersects`.** Q-20 was closed on *two*. `GeometryPredicates` is a
third, on flat doubles with no epsilon; it is checked against PostGIS on real polygons and on the
whole provider (§4), and where it and PostGIS differ is recorded rather than tolerated.

**The object id of a file with no unique integer column is its row number**, which is stable exactly
as long as the file is. A replacement that reorders rows moves every id. The file's version changes
with it, a column of the file's own is offered first whenever one is unique, and a replacement whose
identity is no longer unique is refused rather than answered.

## 4. Evidence

All on DuckDB 1.5.5 through DuckDB.NET 1.5.5, on 2026-09-13.

| Claim | Evidence | Source |
|---|---|---|
| Core DuckDB reads GeoParquet with no extension | Loaded extensions after start: `core_functions`, `icu`, `json`, `parquet`, `autocomplete`. The `GEOMETRY` type, `st_aswkb`, `st_geomfromwkb`, `st_intersects_extent`, `st_crs` and `st_setcrs` are core; `st_xmin` and `st_extent_agg` answer *exists in the spatial extension* | spike, Windows x64 |
| It runs on the deployment's architecture | A GDAL-written GeoParquet file counted and read as WKB on the VPS, linux-arm64, from the package's own native library | spike on the arm64 VPS |
| The sandbox confines reads and locks itself, and does not stop a write inside the folder | Read outside, `glob` outside, `INSTALL spatial`, `LOAD spatial`, `SET enable_external_access = true`: permission errors. `COPY (select 1) TO '<folder>/x.csv'` and `ATTACH '<folder>/x.duckdb'`: succeeded. `allowed_directories` set after `enable_external_access = false` refuses the folder itself | spike; `The_folder_s_DuckDB_cannot_read_outside_the_folder_or_unlock_itself` |
| Binding quirks the provider depends on | `GEOMETRY` cannot be read by the binding (type 40), so geometry is selected as `st_aswkb`; a duplicated connection arrives closed; named `$name` parameters bind; a `DateTimeOffset` binds as `TIMESTAMPTZ` and compares with a `DATE` column correctly only with `TimeZone = 'UTC'`, which the folder sets before locking; `percentile_cont … within group`, `distinct on` and `list` parameters work | spike |
| DuckDB refuses a GeoParquet file without `geometry_types` | *Geoparquet column 'geom' does not have geometry types* — so the probe names the missing key instead | `A_missing_geometry_types_list_is_the_file_s_fault_and_says_so` |
| `GeometryPredicates.Intersects` agrees with PostGIS's `ST_Intersects` | 3,899 pairs from 300 real OSM polygons against their own boxes, quarters, corners, a vertex, a centre, a diagonal line and the next polygon; 2,400 intersect; **0 disagreements**. **Falsified**: the point-in-ring test forced false gives 300 | `Intersects_matches_PostGIS_on_real_polygons_and_the_shapes_sent_against_them`, VPS corpus |
| The whole provider answers what PostGIS answers | 1,500 PostGIS-made polygons (a fifth with holes) at web-Mercator magnitudes, written as a GDAL-shaped file: 44 intersects filters (polygons, their vertices, lines, rectangles), boxes, where clauses, paging over a tied order, distinct, a box in longitude and latitude with output projected, ids, extents and eight statistics — **3 of 3 pass**. **Falsified**: polygon containment removed from the predicate fails 2 of 3 | `GeoParquetAgainstPostgisTests`, VPS datastore |
| The mechanism around the predicate | 61 tests: covering-column rounding at 10⁷, row-number identity, refusals, sandbox, replaced files. **Falsified** twice: widening off fails the far-magnitude test; the exact test off fails the L-shape test | `Graticula.Providers.DuckDb.Tests`, Windows and linux-arm64 |
| Every face serves a file written by GDAL | Three OSM files from the importer's own GDAL (30,000 polygons in CRS84, 20,000 roads in EPSG:3857, 3,405 places): register, refuse four bad registrations, refuse a wrong reference and a non-unique identity at publish, then FeatureServer document (`Query` only, no distance), count, where, ids, grouped statistics, envelope, exact intersects **245 against the box's 294**, extent, `Within` and distance refused by name, a 3857 file projected to 4326, `applyEdits` refused; OGC API collections, bbox and property filter; WFS capabilities and GetFeature; WMS capabilities and a drawn PNG; `generateRenderer` — **all pass** | `gp-e2e.py` against the fixture server, VPS arm64 |
| Through the FeatureServer, a file and a PostGIS table holding the same rows agree | 47 questions (40 random triangles, envelopes, lines and points; five where clauses; grouped statistics; an extent), **29,367 feature ids compared**: two envelope queries differ, by five features PostGIS alone returns. Each is **outside the envelope by 0.87 × 10⁻⁶ to 3.6 × 10⁻⁶ degrees** (0.1–0.4 m), and PostGIS's own `ST_Intersects` says false where its `&&` says true — the single-precision box widening Q-20 measured. The file's answer is the exact one | `gp-compare.py`, VPS arm64 |
| An independent security review of the working tree, before commit | One high finding and four low, all repaired in the same change and each with a test: **no statement deadline** on this provider (the PostGIS path's thirty seconds now applies, lowered by the service; a cancelled DuckDB command was measured stopping within milliseconds with its connection reusable), an **unbounded list of matched identities** (refused past a million), **no vertex cap on a filter** compared in process (GeometryServer's 130,000), a stored locator compared **as text before it was canonical** and **links not refused** between the root and a file, **DuckDB instances never closed** after a probe, removal or move, a malformed `geo` value that **threw instead of reporting**, and path **pattern characters** DuckDB would expand. No caller-controlled text was found reaching DuckDB SQL, and no path reaching an anonymous caller | review of 2026-09-13; `A_query_past_its_statement_timeout_is_stopped_and_says_so` falsified by removing the deadline |
| At a million features, where row groups matter | One million OSM polygons, numbered in geohash order, served as a PostGIS table (706 MB with its index) and as the GeoParquet file GDAL wrote from it (507 MB, 16 row groups, covering column). 41 questions through the FeatureServer, median of two warm rounds, file against table: small envelopes, triangles and points **125–204 ms / 19–41 ms**; a page of 1,000 features with geometry **683 / 330**; where-clause counts **36 / 155**; the whole count **32 / 103**; grouped statistics **41 / 196**. Id sets compared over 17,114 features: three envelope questions differ by six features, **all six returned by PostGIS alone** — the direction the 30,000-row run traced to its single-precision box, not traced one by one here. **Before the exact box test moved into this process the spatial questions took about 470 ms**: `st_intersects_extent` on the geometry column made DuckDB convert every surviving row group's geometry, measured at 440 ms against 35 ms for the covering filter alone | `gp-bench.py` and a DuckDB.NET probe of the statement, VPS arm64 |
| What it costs | Median of two warm rounds, loopback, 30,000 polygons in one row group — file against table: where **27 / 29 ms**, statistics **28 / 31**, envelope **50 / 21**, point **62 / 23**, line **76 / 45**, triangle **80 / 42**, extent **60 / 37**. Before ids and counts were answered from the exact test's own identities rather than a second scan, point was 68, line 83 and triangle 89 | `gp-compare.py`, VPS arm64 |

## 5. Decision

**A data source may be a folder of GeoParquet files, inside a root the deployment names, and each
`.parquet` file in it may be published as a read-only layer served by DuckDB in the serving
process.** Concretely:

1. **The source.** `data_source.kind` is `geoparquet` (migration 46) and its sealed locator is
   `geoparquet:<absolute folder>` — the locator names its engine, so every place that has only the
   string routes it (`GeoParquetLocator`). Registration takes `kind` and `path`; a path outside
   `Graticula:GeoParquetRoot` is refused, as are a folder described with host fields and a locator
   written by hand. A source keeps its kind. Unset, the root switches the feature off and every
   refusal says which setting turns it on.
2. **The engine.** One in-memory DuckDB per folder, confined before first use — memory limit
   (1 GB), threads (2, both settings a deployment can raise), `TimeZone` UTC, `allowed_directories`, autoinstall and autoload off, community extensions
   off, external access off, configuration locked. **Core only; the spatial extension is never
   loaded.** Confined to `Graticula.Providers.DuckDb` by `NativeDependencyTests`.
3. **The file.** GeoParquet's own `geo` metadata decides the layer: the primary column, WKB only,
   an absent `crs` as CRS84 and a null one as unknown and unpublishable, a PROJJSON identifier as the
   EPSG code, one geometry family. The table name is the file name, the schema is `main`. Files that
   cannot be layers are listed with the reason.
4. **The where clause** is emitted again from its tree (`ParsedWhere.Predicate`) with DuckDB's
   placeholders — D-162's seam.
5. **Spatial filters.** Intersects, envelope-intersects and index-intersects are answered: the box
   test runs in DuckDB (the covering column, widened past single-precision rounding, then
   `st_intersects_extent` exactly), and the exact test runs in process with
   `GeometryPredicates.Intersects`, whose identities the rest of the query is then answered over.
   Contains, within, crosses, overlaps, touches, relate and distance are refused by name
   (`QueryNotSupportedException`, 400), and the layer document does not claim distance.
   **Bounded like a database query**: the PostGIS path's thirty-second statement deadline, lowered by
   the service and answered 504 when passed; a filter geometry of at most 130,000 vertices; and at
   most a million matched features, past which the filter is refused rather than held.
6. **References.** A filter stated in another reference and an output reference go through the
   datastore's PROJ (`IProjector`), so a file and a table in the same reference answer with the same
   numbers. `maxAllowableOffset` is not applied — the stored shape satisfies it — and
   `geometryPrecision` rounds coordinates.
7. **Summaries.** Counts, ids, extents and statistics are answered — through a new port,
   `IFeatureSummaries`, which PostGIS implements too, replacing three casts to the PostGIS provider.
8. **Identity.** An integer column measured unique and never null, a column of the file's own first,
   or `file_row_number`. Verified at publish and again whenever the file changes.
9. **Read-only.** `Writable` is false, so capabilities are `Query`; editing, attachments, related
   records, vector tiles and schema editing are refused, the last line of each in
   `LayerConnections`.
10. **Publish** checks the request against the file — reference, geometry column and family,
    identity — on both publish routes; validity and the declared reference, which are PostGIS's
    measurements over a table, are not asked.
11. **Deployment.** The image carries DuckDB's Linux libraries only, with its two licence notices,
    and creates `/data/geoparquet` as the root; compose mounts `./geoparquet` there read-only.
12. **Paths.** A folder is canonical before it is compared with the root, at registration and at
    every use; a folder whose path holds a character DuckDB reads as a pattern is refused, as are
    a link between the root and the folder and a file that is a link. A folder's DuckDB closes when
    its source is removed, moved or quiesced, and probing a folder nothing reads opens and closes
    its own.

## 6. Consequences

**Positive.** A file becomes a layer without an import. D-162's seam exists, and the query model
has been exercised by an engine that is not PostgreSQL for the first time — which found three casts
to the PostGIS provider and turned them into a port. Attribute queries and statistics cost what they
cost on PostGIS. The exact answer to an envelope query is exact, where PostGIS's is up to a
single-precision step wide.

**Negative.** At a million features a small spatial query costs **four to six times** what PostGIS
costs (125–204 ms against 19–41) — §4 — and revisit trigger 5 fired on it; see §9. DuckDB's calls are synchronous, so a running GeoParquet query holds a thread-pool
thread for its duration — bounded by the connection budget and the thirty-second statement deadline,
not removed. Spatial queries are about twice as slow as PostGIS at 30,000 features in one row group. Five relations and distance are refused.
A native parser runs in the serving process. The image grows by DuckDB's two Linux libraries
(68 MB for x64, 61 MB for arm64, uncompressed). The WFS capabilities list spatial operators for the
whole service, and a GeoParquet layer refuses some of them (condition 3). A layer on a file with no
unique integer column has ids that move if the file is rewritten in another order.

**State.** *Catalogue*: a `data_source` row of kind `geoparquet` whose sealed locator is the folder's
path (migration 46), and ordinary `layer` rows naming schema `main` and the file as the table.
*Runtime, node-local*: one in-memory DuckDB per registered folder, opened on first use and closed
with its source (`GeoParquetSources`), and per folder a cache of each file's metadata keyed by its
length and modification time. Nothing is shared between nodes and nothing needs to be: every node
reads the same files, and a node that has not opened a folder yet opens it on its first query.
*Files*: the GeoParquet files themselves, which this server never writes.

**Ports created.** `IFeatureSummaries` (Core) — extents, ids and statistics of a query. DuckDB
stays behind `IFeatureSource`, `IFeatureSummaries` and `IFeatureVersions`; no DuckDB type reaches
Core, a face or a writer. `QueryNotSupportedException` (Core) carries a provider's refusal to a 400.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-016 | The serving process never loads GDAL | holds — the spatial extension is never loaded, and `LOAD` is refused by the locked configuration |
| — | A GeoParquet folder is placed by the operator and never uploaded through the API | **the line §3 rests on**; recorded here rather than as a new assumption row because it is a property of this build's API, and the day it changes is a revisit trigger |

## 8. Dependencies

**Depends on** — [ADR-008](ADR-008-query-engine.md) (query model, D-162),
[ADR-003](ADR-003-geometry-engine.md) (flat geometry primitives), [ADR-009](ADR-009-raster-engine.md)
§2.2 (the untrusted-parser rule), [ADR-022](ADR-022-geometry-server.md) (the datastore's PROJ),
[ADR-059](ADR-059-quiescing-a-data-source.md) (quiesce, which a folder takes part in),
[ADR-063](ADR-063-a-field-list-may-differ-from-the-table.md) (field overrides, which apply unchanged).

**Depended on by** — none yet.

## 9. Revisit triggers

- **A GeoParquet file can be uploaded through the API** — the §3 line is crossed; DuckDB moves to a
  child process (Alternative C) or the upload is converted instead (Alternative B).
- **A crash in `libduckdb` is observed in the serving process** — the same move.
- **Remote object storage is asked for** (`s3://`, `https://`) — `httpfs` is an extension, and
  external access is exactly what the sandbox switches off; that is a new decision, not a setting.
- **A client needs contains, within, touches, crosses, overlaps, relate or distance on a file** — the
  in-process predicate set is extended with PostGIS as its oracle, or the layer is imported.
- ~~**Spatial p50 on a GeoParquet layer exceeds three times PostGIS's on the same rows at a million
  features** — condition 4's measurement decides whether the file path is fit for anything but
  reference layers.~~ **Fired the night it was written, 2026-09-13, and answered rather than
  dismissed.** 125–204 ms against 19–41 ms is four to six times. The answer is the one §2 already
  gave for Alternative B, now with its number: **a GeoParquet layer is a reference layer** — drawn,
  identified, counted, classified — and a layer queried spatially at a high rate belongs in the
  datastore, where the geometry is indexed. What the measurement also says is that the file path is
  not slow everywhere: counts, where clauses and statistics are three to five times *faster* than
  PostGIS on the same million rows, because they read columns rather than rows. The trigger that
  replaces it: **a small spatial query on a GeoParquet layer passes 500 ms at median**, which is
  where an interactive map stops feeling like one.
- **DuckDB.NET changes how `GEOMETRY` is returned** — the binding workaround (`st_aswkb`) is load
  bearing and pinned by version.

## 10. Dissent

None recorded, and the absence has a reason worth stating: this was decided overnight without a
reviewer. Q-87's author recommended *job worker only* for a reason this ADR shows to be about the
spatial extension rather than DuckDB; whether serving a file directly is *a convenience nobody has
justified* — Q-87's last words — is the owner's to answer, and §11 asks.

## 11. `INFERRED`, for the owner's confirmation

1. **The shape.** The request was to *set up the relationship* between DuckDB and GeoParquet. This
   ADR reads that as *serve GeoParquet files in place as read-only layers*, not *convert GeoParquet
   into the datastore* (which needs no DuckDB). It reverses Q-52's *never served from the file* for
   GeoParquet and Q-87's *job worker only*.
2. **In the serving process**, on the §3 argument that the files are the operator's, rather than in a
   child process.
3. **Refuse rather than approximate** the relations the core cannot answer, rather than load the
   spatial extension.
4. **`file_row_number` as an identity** for a file with no unique integer column.
5. **Off unless the deployment names a root**, and the image's root is `/data/geoparquet`.

## 12. Conditions

1. **The provider is checked against PostGIS in CI, not only on the VPS.** `GeoParquetAgainstPostgisTests`
   carries no `Needs` trait, so the platform suite runs it against the CI datastore. **DISCHARGED
   2026-09-13** by CI run 34729938403 on the branch: the platform and providers job passed 392 of 392
   with the class in it, on `postgis/postgis:16-3.4`, and the provider's own 71 tests passed on the
   runner's linux-x64 library.
2. **An ArcGIS client adds a GeoParquet layer and draws and queries it.** Not measured — the same gap
   ADR-065 condition 2 records, for the same reason.
3. **WFS and OGC API Features advertise, per layer, only the spatial operators the layer answers.**
   Today the WFS capabilities list operators service-wide, and a filter a GeoParquet layer cannot
   answer is refused at query time with a sentence rather than not offered. [D-263](../architecture-debt.md).
4. **The spatial cost is measured at a size where row groups matter** — a million features or more,
   written by GDAL with its covering column — so §6's *twice as slow* is a number with a size attached
   rather than a sample of one. **DISCHARGED 2026-09-13**, and it corrected the sentence it was written
   to check: at a million polygons in 16 row groups, a small spatial query is four to six times
   PostGIS's time, not two (§4). The measurement also found the reason it had been twenty times —
   DuckDB converting the geometry column for its own box test — and the exact box test moved into
   this process in the same change.
5. **The image starts DuckDB on both architectures.** The release workflow builds `linux/amd64` and
   `linux/arm64`, and the Dockerfile fails if either library is missing; that a container on each
   actually opens a folder is not yet observed.
