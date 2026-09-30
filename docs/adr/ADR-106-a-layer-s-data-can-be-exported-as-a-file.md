# ADR-106 — A layer's data can be exported as a file

| | |
|---|---|
| **Status** | `DRAFT` |
| **Confidence** | `MEDIUM` — every format rests on a GDAL driver measured present and able to create on both platforms the server ships for (§4), and the job, budget, download and retention are ADR-098's, built and run; nothing in this ADR is built, no export has been written, and no reader (ArcGIS Pro, Excel, Google Earth) has opened one |
| **Decided** | 2026-09-30, by owner decision, answered as four questions. **1.** The result is **a downloadable file kept for a limited time**, as a tile package is — **not a new portal item**. ArcGIS Online's `content/users/{u}/export`, which the ArcGIS Python API's `item.export()` calls, makes an item, and an item needs [ADR-056](ADR-056-an-item-is-its-own-thing-and-a-service-is-one-kind.md)'s item table, which is not built; `item.export()` will not work against this server. **2.** **The service's owner and administrators may always export; any other signed-in reader only when the service offers `Extract`**, which the server now enforces for the first time; **anonymous callers never**. Queries stay open exactly as today. **3.** **Packaged as ArcGIS Online packages it**: the caller chooses the service's layers; GeoPackage, File Geodatabase, KML and Excel hold every chosen layer in one file; Shapefile, CSV and GeoJSON write a file per layer inside one `.zip`. **4.** **Attachments are not in the first version.** The owner asked for the eight formats ArcGIS Online offers: Shapefile, CSV, KML, Excel, File Geodatabase, GeoJSON, Feature Collection, GeoPackage. Every number, name, route and shape below that the owner did not state is this session's design and is marked **INFERRED** (§12). |
| **Depends on** | [ADR-098](ADR-098-vector-tiles-can-be-exported-as-a-package.md), [ADR-011](ADR-011-job-system.md), [ADR-009](ADR-009-raster-engine.md) §2.2, [ADR-037](ADR-037-job-workers-come-in-two-kinds.md), [ADR-031](ADR-031-service-capability-configuration.md), [ADR-063](ADR-063-a-field-list-may-differ-from-the-table.md), [ADR-065](ADR-065-domains-and-subtypes.md), [ADR-087](ADR-087-domains-are-shared.md), [ADR-077](ADR-077-z-and-m-ride-beside-x-and-y.md), [ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md), [ADR-102](ADR-102-the-studio-item-is-one-page-and-every-setting-has-one-home.md) |
| **Amends** | — while a draft, which amends nothing. What it will amend on acceptance, and the note each amended ADR then gets, is in §6. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

> **Progress, 2026-09-30.** The owner decided two more things the same day. **Unchosen Extract is off**: a reader
> other than the owner and administrators exports only where the owner has chosen Extract, as §5.5 and the top of
> §12 proposed. **This is built on [ADR-107](ADR-107-a-layer-is-exported-as-geopackage-shapefile-or-workbook.md)**,
> which another session had built without knowing of this draft: its synchronous `POST …/layers/{id}/export` went
> first, and this ADR's job, packaging and the rest follow on top of it. Since then the same route also writes
> File Geodatabase, KML, CSV and GeoJSON (ADR-107 §5.1); Extract is now enforced and announced as §5.5 says (the
> catalogue drops it where the owner has not chosen, `capabilities` names it where the service offers it, Settings
> draws it unticked until chosen). Still to come from this ADR: Esri JSON, and the job with its 2,000,000 rows and
> multi-layer packaging.

## 1. Context

Studio's Overview › *Export data* (`console.js` `exportServiceData`, ~5591-5651) is a loop in the browser. It pages
the FeatureServer's `query` 2,000 rows at a time by offset, stops at 100,000 rows, and writes one of two files for one
layer: CSV (attributes only, epoch-millisecond dates as the JSON answer has them, a UTF-8 byte-order mark so a
spreadsheet reads Turkish letters) or GeoJSON (WGS 84). Anything a GIS analyst opens first — a Shapefile, a
GeoPackage, a File Geodatabase — it cannot write, a layer of 100,001 rows is silently a layer of 100,000 with a sentence
saying so, and every row travels to the browser before one byte is saved.

ArcGIS Online answers the same button with eight formats, several layers of one service at once, and a job on the
server. The owner asked for those eight.

**The gate was never the server's.** `Extract` is one of the five words a service's ceiling may hold
(`ServiceCapabilityLimits.cs:41`) and one of the four an owner may offer (`AdminEndpoints.cs:4533`,
`OwnerOperations`; migration 66's `editing_offered`). Nothing reads it: `RefusedByCeilingAsync` is documented for
`Extract` (`Program.cs:4913`) and called only with `Query` and the edits, and `PrivilegedCapabilities`
(`Program.cs:5045`) offers `Query` and the three edits and never `Extract`, so `Restrict`, which only intersects,
cannot put it in any capabilities string. The console's rule (`console.js` ~5071-5073: the owner, or a reader when the
service document says `Extract`) is therefore, as built, *the owner only* — the reader half of ADR-102's repair can
never fire — and the Settings hint that *what you turn off here is refused to every client* is untrue of *Export data*.
And a gate in the browser over a public `query` protects nothing in any case.

What exists makes most of this a matter of joining pieces:

- **GDAL is in the solution and out of the serving process.** `MaxRev.Gdal.Core` 3.13.1.534 with the Minimal Windows
  and Linux runtimes is referenced only by `Graticula.Import.Reader`, an executable the host spawns as a child
  ([ADR-009](ADR-009-raster-engine.md) §2.2 as amended: *the serving process never loads GDAL*). Its protocol is one
  JSON request on stdin, replies and NDJSON on stdout (`Program.cs:36-41`); it already writes files — `convert`
  (`VectorTranslate` to Parquet, :666-690) and `fixture` (OpenFileGDB, :226-249). The host's side,
  `GeodatabaseReader`, finds it beside the host in `importer/` (:132-138), caps its managed heap at 512 MB (:56, :191)
  and its working set at 2 GB polled every 250 ms (:84, :92), runs it `BelowNormal` (:567) and kills it on a deadline
  (:263-290). It is handed paths, never a database connection.
- **Reading rows without a request.** `IFeatureSource.ReadAsync` (`FeatureQuery.cs:344`) streams a query's features;
  `ServiceContexts.GetAsync(layer)` gives the source and the *served* description, the one ADR-063's overrides have
  already narrowed — as `TileSeeder` and `TileExporter` take it, with no HTTP context. `TableAsync` (:262) is the
  table before the overrides, and says *nothing that serves data may call this*.
- **The job, the file and its address** — ADR-098 built all of it for tile packages (§5, "reused").

## 2. Alternatives considered

### Alternative A — a server job: the host reads the rows and stages them, the reader child writes the format *(chosen)*

A new job kind whose worker reads each chosen layer through `IFeatureSource` into a staging file per layer, then asks
`Graticula.Import.Reader` — a new op, `export` — to write the target format from those staging files with GDAL.

**Argument for.** Each side keeps the property it already has: the host holds the database connection, the served field
list and the sharing rules; the child holds GDAL and nothing else, so a GDAL crash or a runaway driver costs a killed
child and a failed job, never the server. Every row is read the way a query reads it — the same admission, the same
statement timeout, the same hidden columns left out.

**Argument against.** Every row is written twice (staging, then the format) and read three times; a staging format has
to be chosen and written by the host without GDAL.

### Alternative B — the reader child queries the database itself

**Argument for.** One pass: GDAL's PostgreSQL driver reads the table and `VectorTranslate` writes the format.

**Argument against.** It hands a GDAL process a connection string, it reads the *table* — hidden columns and all —
not the layer, and it cannot read a GeoParquet or DuckDB layer or a registered source through this server's
admission and budget. It is `TableAsync` with a second implementation. **Chosen against.**

### Alternative C — keep the browser loop and add formats in JavaScript

**Argument for.** No job, no disk, no budget: the caller's browser pays.

**Argument against.** Shapefile, GeoPackage and File Geodatabase in a browser mean a WebAssembly GDAL of tens of
megabytes in the console; the row cap stays a browser's memory; `Extract` stays unenforceable. **Chosen against.**

### Alternative D — the export is a portal item (`content/users/{u}/export`)

**Argument for.** It is exactly what ArcGIS Online does and what `item.export()` and Pro's portal calls expect; the file
would have an owner, a sharing level and a place in *My content*.

**Argument against.** This server's items are projections of service, coverage and web-map rows; nothing is stored
([ADR-040](ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md)), `/sharing/rest` has no `addItem` and no
`content/users/{u}/export` (`PortalEndpoints.cs:71-178`), and an item with nothing behind it is exactly ADR-056
condition 2 — undischarged because the table does not exist. **Chosen against by the owner (decision 1)**; it is the
next step once ADR-056 is built (§9).

### Alternative E — the reader writes with `VectorTranslate`, as `convert` does

**Argument for.** One call per layer; GDAL's own `ogr2ogr` behaviour for every driver.

**Argument against.** No staging format carries field domains, so `VectorTranslate` cannot give GeoPackage or File
Geodatabase their coded values; Shapefile names would be GDAL's truncation, not a rule this ADR can write down; and
per-layer options differ by format. **Chosen against — INFERRED**: the `export` op creates the dataset, adds the domains
(`AddFieldDomain`), creates each layer and field with its domain named, then copies features from staging in
transactions — what `ogr2ogr` does inside, with the choices made here.

## 3. Counterarguments to the preferred option

- **`Extract` is a convenience gate, not a data-protection boundary.** A reader refused an export may page `query` for
  the same rows, as in ArcGIS Online, where `Extract` also governs export and not query. What the gate buys is a
  publisher's statement of intent and a bound on who may spend this server's disk. A deployment that must keep data in
  must stop sharing it.
- **Making `Extract` real changes what existing services do.** Today no reader can reach *Export data*. If a service
  with nothing chosen (`editing_offered` null) counted as offering `Extract` — which is how the ceiling reads null and
  how the Settings checkbox draws it today — every reader of every existing service could export on upgrade. §5.5
  decides the other way (INFERRED): `Extract` is offered only where somebody named it. The console's checkbox then has
  to draw from that rule, and the rule differs from the three edits' on purpose.
- **Advertising `Extract` may invite a call this server does not answer.** As recalled from the ArcGIS REST reference
  (not re-read this session), `Extract` is also what lets a client call `createReplica` with `syncModel=none`. This
  server serves no `createReplica`; a client that tries gets a 404. §9 names the trigger.
- **The export is not a snapshot.** Pages are separate statements; a row edited during the export may appear in either
  state. Keyset paging (§5.3) means no row appears twice or is skipped for moving position, but a consistent copy would
  need one transaction held open for the whole read — a pinned connection for minutes, against ADR-011 §3.6.
- **GDAL's Excel writer is unmeasured at size.** A sheet is built in memory until the file closes (believed, not
  measured), and the child's 2 GB ceiling will kill a large one. Condition 5 measures it; until then the Excel cap is
  the sheet's own row limit and a kill is a failed job that says so.
- **The directory and the console's download are D-282's**, as for tile packages: per server unless shared, and a
  download buffered in the browser because the console signs with a header a link cannot carry.
- **Eight writers, each with its own truncations.** Shapefile's ten-byte names and 254-byte text, Excel's 32,767-character
  cell, KML's lost types: every one is a place data silently changes shape. §5.6 says what each does, and the dry run
  names what a given export will lose before it starts.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| ESRI Shapefile, GPKG, KML, LIBKML, CSV, GeoJSON, OpenFileGDB, XLSX, ODS, FlatGeobuf, Parquet, SQLite, GML, DXF and MapInfo File are present with `DCAP_CREATE=YES`; the Esri SDK driver `FileGDB` is absent, so OpenFileGDB writes File Geodatabases; GDAL 3.13.1, 213 of 214 drivers | A probe run through `MaxRev.Gdal.Core` 3.13.1.534 on Windows and inside the showcase's server image (linux-arm64) | **measured 2026-09-30** |
| The reader's protocol and ops (`drivers`, `ping`, `layers`, `convert`, `features`, `fixture`) and the host's bounds on it | `Graticula.Import.Reader/Program.cs:36-41, 226-249, 666-690`; `Graticula.Host/GeodatabaseReader.cs:56, 84, 92, 132-138, 191, 263-290, 567` | read 2026-09-30 |
| Rows are read by `IFeatureSource.ReadAsync`; a page is at most 50,000; a query takes an output SRID, a where clause, an order and the ordinates to keep | `FeatureQuery.cs:32, 85-102, 215, 300, 344-395` | read 2026-09-30 |
| `GetAsync` applies the field overrides; `TableAsync` does not and must not serve | `ServiceContexts.cs:221-262` | read 2026-09-30 |
| `Extract` is stored and never enforced or advertised | `ServiceCapabilityLimits.cs:41, 181-195`; `AdminEndpoints.cs:4533`; `Program.cs:4913, 5045-5052`; migration 66; `console.js` ~5071-5073, ~6664-6669 | read 2026-09-30 |
| The job core, the export worker's pattern, the budget under an advisory lock, the token, the cookie rule, the settings | `IJobStore.cs:16-56, 182-340`; `TileExporter.cs`; `ITileExportStore.cs:100-121`; `VectorTileExportEndpoints.cs:454`; `HostSettings.cs:214-238, 628-636` | read 2026-09-30 |
| Schema at v66; migration 64 was the last to widen `job_kind_known` | `PlatformMigrations.cs:33, 101-103, 216` | read 2026-09-30 |
| The data model: one `Date` field type, coded-value domains (shared since migration 55), subtypes, Z/M, attachments | `LayerDescription.cs:72`; `Catalog/FieldDomain.cs:81, 100`; `Catalog/LayerSubtypes.cs:63, 109`; `GeometryOrdinates`; `IAttachmentStore.cs:58` | read 2026-09-30 |
| No item table, no `addItem`, no `content/users/{u}/export` | `PortalEndpoints.cs:71-178` | read 2026-09-30 |
| ArcGIS Online's *Export Data* offers these eight formats, makes a new item, and is open to the owner and administrators always and to others when the layer allows export (`Extract`) | ArcGIS Online documentation | **recalled, not re-read this session** |
| A dBase field name is at most 10 bytes; a text field at most 254 bytes; a `.shp` or `.dbf` at most 2 GB; one geometry type per file; `.cpg` names the encoding | GDAL *ESRI Shapefile* driver documentation; Esri *Shapefile Technical Description* | recalled, not re-read this session |
| OpenFileGDB and GPKG write coded field domains through OGR's field-domain API; CSV's `GEOMETRY=AS_XY`/`AS_WKT` and `WRITE_BOM`; GeoJSON's `RFC7946=YES` | GDAL driver documentation | recalled, not re-read this session; **that the C# bindings of 3.13 expose `AddFieldDomain` is INFERRED** (condition 1) |
| GeoJSON is WGS 84 longitude-latitude with Z and no M | RFC 7946 §3.1.1, §4; `GeoJsonWriter.cs:26, 264` | read 2026-09-30 (the writer) |
| An Excel sheet holds 1,048,576 rows and a cell 32,767 characters | Microsoft, *Excel specifications and limits* | recalled, not re-read this session |
| Everything else: the staging format, the routes, the names, the caps, the per-format rules | — | **INFERRED** — this session's design |

## 5. Decision

### 5.1 An export is a job

- A new job kind, **`feature.export`** (`JobKind.FeatureExport`); the next free migration — **67 unless another has
  landed first** — widens `job_kind_known` and adds the table **`feature_export`** (§6, State). **INFERRED.**
- **`FeatureExporter`**, a `BackgroundService` shaped as `TileExporter`: claim with `FOR UPDATE SKIP LOCKED`, the lease
  kept by `JobLeaseKeeper`, a checkpoint every two seconds that learns of a cancel, the sweep, **one export at a time per
  process**, and nothing on a request thread. Its own worker rather than `TileExporter`'s, so a long tile package does not
  queue a small CSV (INFERRED).
- **`JobRerun.Harmless`: restart, not resume.** A lost lease queues it once more; a second loss fails it. A second run
  reads from the first row and writes from nothing.

### 5.2 Two stages, and who holds what

1. **The host reads.** For each chosen layer: `ServiceContexts.GetAsync(layer)` — the served description, never
   `TableAsync` — then pages through `ReadAsync` and writes **a staging file per layer**. Each page is admitted as a
   query is (`LayerConnections`), so an export is polite to the source as ADR-011 §3.6 requires.
2. **The reader child writes.** `GeodatabaseReader`'s spawn gains an op **`export`**: one JSON request naming the
   staging paths, the target path and driver, and per layer its name, its fields (source name, target name, type,
   alias, domain), its geometry type and reference, and the domains; progress comes back as NDJSON lines
   (`{"layer", "rows"}`), the last line is the result or the failure. It opens no connection and reads nothing but
   the paths it is given. Heap, working set, priority and kill are the reader's existing bounds; the deadline is
   `Graticula:FeatureExportTimeoutMinutes`, **60** (INFERRED).
3. **GeoJSON and Feature Collection skip the child.** They are the encodings the server already serves
   (`GeoJsonWriter`, `FeatureServerQueryWriter`), so the host writes them straight from the rows and an exported
   feature is byte for byte the served one. GDAL has no Esri JSON writer in any case. **INFERRED.**

**The staging format is FlatGeobuf** (**INFERRED**), one file per layer, written by the host from the published
specification with no new package, as ADR-098 wrote compact cache bundles and PMTiles:

- it streams — a header, then features, with no index required — so the host holds one page, not a layer;
- it carries every OGC geometry type with Z and M, typed columns (integers of each width, double, string, date-time,
  binary) and the reference as an EPSG code or WKT, so nothing is lost before the target format decides what to drop;
- GDAL reads it (measured present), and the child reads it with the same OGR calls it uses for every layer.

**GeoJSONSeq** was the cheapest to write (the host already writes GeoJSON) and is chosen against: it has no M, no
integer width, no date type and one reference. **GeoParquet** carries all of it and is chosen against: the host would
write Parquet through DuckDB or a new dependency, for a file that lives minutes.

### 5.3 What is read

- **The request names layers by id, and an optional extent** (an envelope in the layer's reference, Web Mercator or
  degrees — `AdminEndpoints.AreaOfAsync`'s rule, as the tile export's). **No where clause per layer in v1** (INFERRED):
  the owner's decision names layers; a filter is the revisit it looks like (§9). Group layers are not layers.
- **Keyset paging, not offset** (INFERRED): `where <oid> > last order by <oid>`, `FeatureQuery.MaximumLimit` rows a page.
  Offset paging reads every skipped row again and misplaces rows when the table moves; a layer without an integer
  object id pages by offset, and the job's messages say so.
- **The served fields only**, the object id among them. Geometry with its ordinates kept (`KeepOrdinates`). Current
  state only; a history moment is not an input.
- **The reference is chosen per format** (§5.6), and when it is not the layer's the host asks the query path for it
  (`OutSrid`), so an exported coordinate equals the one `query?outSR=` would serve.

### 5.4 The cap, the estimate, the budget, the directory

- **Rows are counted first**: `CountAsync` per layer, bounded at the cap plus one. **`Graticula:FeatureExportMaxRows`,
  2,000,000 across the chosen layers** (INFERRED). Over it: 400, `details: ["tooManyRows"]`, the count and the cap.
  Excel is also refused over 1,048,575 rows in one layer (the sheet's limit less its header).
- **The estimate** is the staging size of the first page of each layer (up to 1,000 rows, read at the dry run and at
  the start), scaled to the counted rows, doubled for staging plus output. It errs high, and says it is an upper bound.
  **INFERRED.**
- **One exports budget, shared with tile packages** (INFERRED): a feature export's estimate while it runs and its size
  once written count in the same total as ADR-098's, checked with the insert in one transaction under the same
  advisory lock. The setting becomes **`Graticula:ExportBudgetMB`**, and `TileExportBudgetMB` is still read as its old
  name. Over it: 400, `details: ["exceedsExportBudget"]`, no `force`. A running export whose files pass its estimate
  fails with the same detail rather than taking another export's disk.
- **The same directory, a subfolder**: `<TileExportDirectory>/data/` (INFERRED), so neither export's stray sweep can
  mistake the other's files for its own. Staging under `<token>.staging/`, deleted when the job ends in any state;
  the result written as `<token>.part` and renamed when complete.
- **Per caller, one export queued or running at a time** (INFERRED), a 409 naming it otherwise — the budget bounds the
  disk and this keeps one reader from queuing the worker for everybody (ADR-011 §3.5's fair share, in its smallest form).
- **Retention** is the tile packages': **24 hours** (`TileExportRetentionHours`, read for both), then the sweep deletes
  the file and marks the row removed once it is gone.

### 5.5 Who may export — `Extract`, enforced

- **The owner and administrators** (`LayerAccess.MayManage`) may always export a service they may read.
- **Any other signed-in caller** may export when they may read the service (`ServiceLookup`, its sharing) **and** the
  service offers `Extract`: the administrator's ceiling allows it **and** the owner has named it in `editing_offered`.
  **A null `editing_offered` does not offer `Extract`** (INFERRED; §3, §12) — unlike the three edits, which null leaves at
  the ceiling. Every existing service therefore starts closed to readers, as ADR-098's tile export did, and the Settings
  checkbox draws *Export data* from this rule.
- **Anonymous callers never**, whatever the service offers (owner decision 2).
- **Enforced on the server**, through the existing `RefusedByCeilingAsync(context, layer, "Extract")` for the ceiling
  and the owner's offer, at the start **and again at every download**. A refused reader is answered 403 with the
  sentence; a caller who may not read the service gets the same 404 as every other route.
- **Advertised**: a layer's and the service's `capabilities` include `Extract` for a signed-in caller **where the
  service offers it**, and for nobody else. **Amended 2026-09-30, when it was built:** the draft said the owner and
  administrators too, always; the first build did that and every capability string an administrator read grew
  `,Extract`. Not kept, because ArcGIS Online states Extract as the service's setting and not the caller's, because
  the owner is the ArcGIS Pro connection most often made and Extract is the word ArcGIS reads as `createReplica`'s,
  which this server does not have (ADR-082), and because nothing needs it: the console shows the owner *Export data*
  by asking whether they manage the item, and the export route admits them. The catalogue folds the owner's choice
  into the ceiling as served and drops `Extract` from it where the owner has not chosen (`array_remove` in
  `PostgresLayerCatalog`), so the capability string and the route read one fact. `Editing` and `hasStaticData` are
  derived from the edits and do not move (condition 7).
- **Query is untouched.** A reader of a service without `Extract` still pages `query`; that is the owner's decision,
  and the gate is a convenience gate (§3).

### 5.6 The formats

Common rules (INFERRED unless the owner stated them): **field names as served, not aliases**, with the alias kept
where the format has one (GeoPackage, File Geodatabase); **dates as ISO 8601 in UTC** (`2026-09-30T14:05:00Z`), except
where the format has its own date type and except Feature Collection (below); **Z and M kept where the format holds
them**; **coded-value domains** kept as field domains in GeoPackage and File Geodatabase, and elsewhere the stored code;
**CSV, Excel and KML add a `<field>_desc` column** beside each coded field with the value's description (INFERRED — they
are read by people); GeoJSON, Feature Collection and Shapefile write the code alone, and Shapefile's `.zip` carries a
`domains.csv` (domain, code, description). **Subtypes** are written as the stored code; a subtype's own domains are not
written in v1 (INFERRED). **Text is UTF-8** in every format.

| Format (request token) | Container | Reference | Geometry | Notes |
|---|---|---|---|---|
| Shapefile (`shapefile`) | `.zip`, a set per layer | the layer's, `.prj` | one type per file: a layer of mixed types splits into `<layer>_point`, `_line`, `_polygon` | `.cpg` saying `UTF-8`; names truncated (below); text over 254 bytes cut and counted; a date as ISO text (dBase `D` has no time); over 2 GB per file fails with the reason |
| CSV (`csv`) | `.csv`, or `.zip` of one per layer | WGS 84 | points as `X`, `Y` (and `Z`, `M` when present); other types as a `WKT` column | byte-order mark and CRLF, as the console's CSV today |
| KML (`kml`) | one `.kml`, a `Folder` per layer | WGS 84 | Z kept, M dropped | LIBKML; attributes in `ExtendedData` under a typed `Schema`; the display field as each placemark's name; no styling |
| Excel (`excel`) | one `.xlsx`, a sheet per layer | WGS 84 | points as `X`, `Y` (`Z`, `M`) columns; **other types carry no geometry** — WKT would pass the cell limit | dates as Excel dates in UTC; sheet names cut to 31 characters with the same collision rule as Shapefile |
| File Geodatabase (`filegdb`) | `<service>.gdb` zipped | the layer's | Z and M kept | OpenFileGDB; domains and aliases kept; layer names made legal (letters, digits, underscore; not starting with a digit) |
| GeoJSON (`geojson`) | `.geojson`, or `.zip` of one per layer | WGS 84 (RFC 7946) | Z kept, M dropped | the served writer (§5.2) |
| Feature Collection (`featureCollection`) | `.json`, or `.zip` of one per layer | the layer's | Z and M kept | a FeatureSet — `query?f=json`'s answer, `fields`, `spatialReference`, `hasZ`/`hasM` — so dates are **epoch milliseconds**, as Esri JSON has them |
| GeoPackage (`geopackage`) | one `.gpkg`, a table per layer | the layer's | Z and M kept | domains and aliases kept; the object id is the FID |

Where "or `.zip`" appears, **one layer is the file alone and several are a zip** (INFERRED); Shapefile and File
Geodatabase are always zipped. Every format but GeoPackage also keeps the object id as an ordinary column under the
served object-id field's name (INFERRED).

**Feature Collection is an Esri JSON file, not an item** (INFERRED within owner decision 1). In ArcGIS Online it is an
item type whose data lives in the portal; with no item table there is nowhere to put one. The FeatureSet is the shape
this server already answers, which Pro's *JSON To Features* and the ArcGIS SDKs read, and the host writes it with the
writer that serves it.

**Shapefile names** (INFERRED): a name that fits in 10 bytes of UTF-8 is kept; a longer one is cut at 10 bytes on a
character boundary (a Turkish `ğ` is two bytes); a cut name equal to an earlier one becomes its first 8 bytes and
`_1` … `_9`, then 7 and `_10` … — in field order, so the same layer always gets the same names. Every renamed field is
written beside the layer in `<layer>.fieldnames.csv` (served name, Shapefile name).

### 5.7 Downloads

- **The address is unguessable and governed**, as ADR-098 §5.7: a 32-hex token from the operating system's generator,
  checked where made, by the table's constraint and before it becomes a path; compared in constant time.
- **Access is re-checked at download**: the caller must be the one who started the export or an administrator
  (INFERRED — the file is the caller's copy, as the item would be theirs in ArcGIS Online), and must still pass §5.5. So
  turning `Extract` off or unsharing the service stops every reader's download at once; the owner's own stay.
- **The response** has `Content-Length`, ranges (206), a validator, `Cache-Control: private, no-store`, and a
  `Content-Disposition` naming `<service><suffix>` with an RFC 5987 `filename*` for non-ASCII names; suffixes
  `.gpkg`, `.gdb.zip`, `.kml`, `.xlsx`, `.csv`, `.geojson`, `.json`, `-shapefile.zip`, `-csv.zip`, `-geojson.zip`,
  `-featurecollection.zip`. Types: `application/geopackage+sqlite3`, `application/vnd.google-earth.kml+xml`,
  `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`, `text/csv; charset=utf-8`,
  `application/geo+json`, `application/json`, `application/zip`. **INFERRED.**
- **A cookie alone cannot start an export.** Every route that starts, cancels or deletes is `POST` or `DELETE`, which the
  session cookie never signs (`Authentication.CookieToken`), so a cookie-only `POST` is anonymous and refused; no route
  that writes is a `GET`, so `CrossSiteByCookie` is not needed — condition 8 asserts the refusal.
- **Audited** (INFERRED): `service.data.export` (caller, format, layers, extent, rows counted, estimate, whether by the
  owner or through `Extract`), `service.data.export.download` (with any range), `service.data.export.delete`, and
  `service.data.export.failed` with the first failure.

### 5.8 The routes

Beside ADR-098's `/admin/services/{name}/exports` family and not inside it — **`/admin/services/{name}/data-exports`**
(INFERRED). A kind field on one family was weighed: the rows are another table, the request another shape, the gate
another rule (`Extract`, readers included, against tile packages' owner-only admin route), and one family answering
two permission rules is where one of them is forgotten. `/admin` is the console's API, gated per route: the
stewardship route is already read by any reader of the service.

| Route | Answer |
|---|---|
| `GET /admin/services/{name}/data-exports?folder=` | whether the caller may export and why, the formats, the layers, the cap, the budget and what is held, the retention; the caller's exports (an owner or administrator: every export of the service) |
| `POST /admin/services/{name}/data-exports?folder=` `{format, layers: [ids], extent?}` | **202** with the job and the export; `?dryRun=true` answers the counts, the estimate, whether it fits, and what the format will lose (renamed fields, cut text, dropped geometry, dropped M) |
| `GET /admin/services/{name}/data-exports/{id}?folder=` | status, phase (`reading` / `writing`), rows written of rows counted per layer, size, expiry, failure, download address once written |
| `DELETE /admin/services/{name}/data-exports/{id}?folder=` | cancels a running export or deletes a written one, marks it removed |
| `GET /admin/services/{name}/data-exports/{id}/download?folder=` | the file |

Format tokens also accept ArcGIS Online's `exportFormat` spellings (`Shapefile`, `CSV`, `KML`, `Excel`,
`File Geodatabase`, `GeoJson`, `Feature Collection`, `geoPackage`), so a later `content/users/{u}/export` maps onto
this one to one (INFERRED).

### 5.9 The console

Overview › *Export data* keeps its place (ADR-102 §5.4) and its visibility rule, now the server's (§5.5). The dialog
lists the eight formats, the service's layers as checkboxes (all ticked), the dry run's sentence of what the format
will lose, and *Export*; below, the caller's exports with progress — *reading 120,000 of 480,000 rows*, then
*writing GeoPackage* — polled every two seconds while one runs, size and expiry, *Download* and *Delete* or *Cancel*,
as Settings › Tile layer's table. The VTPK and PMTiles links stay. The browser loop and its 100,000-row cap are
removed. **No extent control in v1** (INFERRED): Overview has no map, and the API's `extent` waits for a map to offer it.

### 5.10 What does not change

`createReplica`, `synchronizeReplica`, `unRegisterReplica`, `extractData` and `extractChanges` stay unserved and
every layer keeps saying `supportsDisconnectedEditing: false` ([ADR-082](ADR-082-offline-sync-is-not-in-v1.md)). An
export is a copy handed out once; nothing comes back. **Attachments are not exported** (owner decision 4).
`query` answers as it did.

## 6. Consequences

**On acceptance this amends** [ADR-031](ADR-031-service-capability-configuration.md) §2a — `Extract` stops being a word the
ceiling stores and nothing reads — and [ADR-102](ADR-102-the-studio-item-is-one-page-and-every-setting-has-one-home.md) §5.4 —
Overview › *Export data* keeps its place and hands its work to the server. Each gets a note saying so in the same
commit that moves this ADR out of `DRAFT`. [ADR-082](ADR-082-offline-sync-is-not-in-v1.md) is not amended: replicas stay
out (§5.10).

**Positive.**
- A layer leaves the server in the format the person asking works in, several layers in one file, with no row cap but
  the operator's, and from a registered PostGIS or GeoParquet layer as from a hosted one.
- `Extract` means what its checkbox says, and a reader can finally be offered export.
- GDAL stays out of the serving process; the job, disk, token, download and retention are ADR-098's, not a copy.

**Negative.**
- Nothing that reads these files has read one (conditions 2 and 3).
- `item.export()` and ArcGIS Online's *Export Data* through the portal do not work here, and say 404.
- Every row is written twice and the disk holds both while it runs.
- Readers now share the exports budget with owners; one reader's large export can refuse another's until it expires.
- Excel drops non-point geometry; Shapefile renames and cuts; KML and GeoJSON drop M — each named by the dry run.
- A FlatGeobuf writer is code this project now owns.

**Reused from ADR-098.** `IJobStore` (create, claim, renew, reclaim, finish), the `BackgroundService` shape,
`JobLeaseKeeper`, the checkpoint, the sweep and stray deletion, `JobRerun.Harmless`, staging and `.part` rename, the
budget under the advisory lock, the 32-hex token, the constant-time compare, re-checking access at download, retention,
ranges, the cookie rule, the audit shape. **Not reused:** the `tile_export` table, `TileExportFormat`,
`TileExportPackage`, the per-service tile export policy columns, the ArcGIS `exportTiles` job and result shapes.

**Ports created.** `IFeatureExportStore` (platform store), beside `ITileExportStore`. GDAL stays behind the reader's
process boundary (`NativeDependencyTests`); no new dependency is adopted.

**State.** *Catalogue*: `feature_export` — service, caller, format, layers, extent, rows counted and written, estimate,
size, token, expiry, removal, failure — deleted with its job and its service; `job_kind_known` widened.
No column on `service`: `Extract` already lives in `capability_ceiling` and `editing_offered`. *Disk*:
`<exports>/data/` — files, and while a job runs its `.staging/` and `.part`. *Runtime*: the worker's current export
and its child process.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | GDAL 3.13's C# bindings expose field domains and OpenFileGDB/GPKG write them as ArcGIS Pro reads them | `UNVALIDATED` — conditions 1, 2 |
| — | A 2 GB working set is enough for GDAL's XLSX and OpenFileGDB writers at the row cap | `UNVALIDATED` — condition 5 |
| — | 2,000,000 rows, a shared 10 GB, 24 hours, one export per caller are useful defaults | Reasoned; not asked of an operator |
| — | Readers who may query do not need `Extract` to protect data, only to state intent | Owner decision 2 |

## 8. Dependencies

**Depends on:** [ADR-098](ADR-098-vector-tiles-can-be-exported-as-a-package.md) (the export machinery);
[ADR-011](ADR-011-job-system.md) (claim, lease, re-run, politeness); [ADR-009](ADR-009-raster-engine.md) §2.2 and
[ADR-037](ADR-037-job-workers-come-in-two-kinds.md) (GDAL in a child); [ADR-031](ADR-031-service-capability-configuration.md)
(the ceiling); [ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md) (the owner); [ADR-063](ADR-063-a-field-list-may-differ-from-the-table.md)
(the served fields); [ADR-065](ADR-065-domains-and-subtypes.md), [ADR-087](ADR-087-domains-are-shared.md) (domains);
[ADR-077](ADR-077-z-and-m-ride-beside-x-and-y.md) (Z and M); [ADR-102](ADR-102-the-studio-item-is-one-page-and-every-setting-has-one-home.md)
(where the button lives).

**Depended on by:** — ([ADR-056](ADR-056-an-item-is-its-own-thing-and-a-service-is-one-kind.md), when built, is where
the export becomes an item.)

## 9. Revisit triggers

- **ADR-056's item table is built** — then `content/users/{u}/export` can make an item from this job's file, and
  `item.export()` works; that item may be the first kind with nothing behind it (ADR-056 condition 2).
- **Somebody asks for attachments** in an export — File Geodatabase and GeoPackage can carry them (owner decision 4).
- An ArcGIS client, shown `Extract`, calls `createReplica` and fails where it did not before (§3).
- Somebody asks to export a filtered subset, or from the map's extent in Studio.
- A measured export of the row cap passes the child's 2 GB ceiling in any format.
- A deployment runs several servers against one catalogue and exports — D-282's trigger.

## 10. Dissent

None recorded. One disagreement inside this session is written down rather than settled: whether a service with nothing
chosen offers `Extract`. §5.5 says no, for ADR-098's reason; the other reading — null is the ceiling, as for the edits,
and as the checkbox draws it today — is simpler and is how the three edits behave. It is §12's first question.

## 11. Conditions

1. **The `export` op writes every format from FlatGeobuf staging**, including the field domains of a layer with a
   shared coded-value domain (ADR-087), and `ogrinfo -al -so` on each output reports the layers, fields, types,
   domains, reference, geometry type and row count the job reported.
2. **Independent readers open every format.** ArcGIS Pro adds the Shapefile, the GeoPackage and the File Geodatabase
   (domains shown as domains in the last two); Excel opens the XLSX; Google Earth or Pro opens the KML; Pro's *JSON To
   Features* reads the Feature Collection; `ogrinfo` reads the CSV and GeoJSON. What differs from §5.6 is written back.
3. **Turkish text survives every format.** A layer with `ğ`, `ş`, `İ`, `ı` in attribute values and in field names is
   exported in all eight formats and read back by condition 2's readers with every letter intact.
4. **A Shapefile name collision** — three fields whose first ten bytes agree, one of them Turkish — gets §5.6's names and
   the `fieldnames.csv` says so.
5. **A layer larger than 50,000 rows** (at least three pages) exports with every object id once, in every format; the
   largest layer the showcase has is exported as XLSX and File Geodatabase and the child's peak working set is recorded.
6. **Every source**: a hosted layer, a registered PostGIS layer and a GeoParquet layer each export, and a hidden field
   (ADR-063) is in none of them.
7. **`Extract` is enforced**: a signed-in reader is refused where the service does not offer it and allowed where it
   does, and refused again at download after the owner turns it off; the owner is allowed either way; an anonymous
   caller is refused either way; `capabilities`, `Editing` and `hasStaticData` are as §5.5 says for each caller.
8. **A cookie-only `POST`** to start an export is refused, and one with a bearer header is not.
9. **The budget is shared**: a feature export that would pass the budget with a tile package held is refused with
   `exceedsExportBudget`, and the stray sweeps of each leave the other's files.

## 12. INFERRED, for confirmation

- **A null `editing_offered` does not offer `Extract`**, so readers of existing services cannot export until the owner
  ticks it (§5.5, §10).
- `Extract` advertised in `capabilities` only to a signed-in caller who may export (§5.5).
- The staging format, FlatGeobuf, written by the host from the specification (§5.2).
- The reader writing through OGR's layer API rather than `VectorTranslate` (Alternative E).
- GeoJSON and Feature Collection written by the host's served writers, not GDAL (§5.2).
- Keyset paging by object id; offset for a layer without one (§5.3).
- No where clause in v1; `extent` in the API and not in the console (§5.3, §5.9).
- WGS 84 for CSV and Excel coordinates; `X`/`Y`/`Z`/`M` columns for points, `WKT` for other types in CSV, none in Excel (§5.6).
- `<field>_desc` columns in CSV, Excel and KML; `domains.csv` beside a Shapefile; subtype domains not written (§5.6).
- A date as ISO text in Shapefile; Excel dates in UTC; epoch milliseconds in Feature Collection (§5.6).
- Shapefile's truncation and collision rule, `fieldnames.csv`, and the mixed-type split (§5.6).
- One layer as the bare file, several as a zip, for CSV, GeoJSON and Feature Collection (§5.6).
- LIBKML, a folder per layer, no styling (§5.6).
- The object id kept as a column, and as the FID in GeoPackage (§5.6).
- `feature.export`, `feature_export`, migration 67 or the next free, `FeatureExporter` as its own worker (§5.1).
- 2,000,000 rows, 60 minutes, one export per caller, the estimate's sampling and doubling (§5.2, §5.4).
- One shared budget renamed `ExportBudgetMB`, the `data/` subfolder, the shared 24-hour retention (§5.4).
- The download belonging to the caller who started it and administrators (§5.7).
- The file names, suffixes and media types (§5.7).
- `/admin/services/{name}/data-exports` as its own family; ArcGIS Online's format spellings accepted (§5.8).
- The audit events and what they record (§5.7).
