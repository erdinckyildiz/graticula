# ADR-213 — A layer may be a table: ArcGIS's standalone table

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-10-21, by owner decision: *"Konumsuz tablo da olsun. ITable gibi."* — tables without a location as well, the way ArcObjects has `ITable` beside `IFeatureClass`. Given in answer to the ArcGIS veteran's second review, whose first missing item it was. |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The ArcGIS veteran's review of 2026-10-21 (finding N1, and its first recommendation) found that this server had no
table without geometry anywhere:

- `FeatureServerMetadataWriter.Service` wrote `tables = []` on every service, and `LayerDefinition`'s constructor
  required a geometry column, as did the catalogue (`layer.geometry_column`, `layer.geometry_type` both `not null`).
- The geodatabase importer — which is also ArcGIS Pro's *Share As Web Layer* path (ADR-191) — refused every class
  without geometry, so a related inspection table, the commonest shape of field data, did not survive a publish; and
  because the import job failed when any one class failed, **a Pro map holding a table or an attachment table failed
  to publish at all**.
- The demo's own "related records" sample had to be a point layer, because a table could not exist.

In ArcGIS a table sits beside the layers in a FeatureServer's `tables` array, shares their numbering, answers `query`,
`applyEdits`, attachments and `queryRelatedRecords` like a layer, and is drawn by nothing. Its documents were read off
Esri's own sample server for this decision (§4).

## 2. Alternatives considered

### A — A table is a layer whose geometry column is null *(chosen)*

`LayerDefinition.GeometryColumn` and `PublishedLayer.GeometryType` become nullable; `IsTable` says which. Every place
that would put a geometry into SQL, a tile or a map now has to say what it does for a table, and the compiler lists
them: there were about 190.

**For.** It is ArcGIS's own model — ArcObjects' `IFeatureClass` *is an* `ITable` with a shape field, and a FeatureServer
table is a layer document with `type: "Table"` and no geometry keys. The query, edit, attachment, history and
relationship paths are shared rather than copied, so a table gets every one of them on the day it exists.
**Against.** A nullable column in the type every face reads is a hazard: a reader that forgets it is a null reference.

### B — A separate `PublishedTable` type and catalogue table

**For.** Nothing that draws could ever be handed a table. **Against.** Query, edit, attachments, history, relationships
and sharing would each need a second implementation or a common base extracted first; the copy that drifts is the one
nobody serves, which is how this repository has lost behaviour before (D-46).

### C — A sentinel geometry type (`GeometryKind.None`)

**Rejected outright.** An enum member flows silently into every `switch` and every SQL builder; it is the nullable
hazard of A without the compiler's help in finding it.

## 3. Counterarguments to the preferred option

- **"Nullable everywhere is a defect waiting to happen."** It is contained by a split, not by vigilance:
  `PublishedService.Layers` holds the layers with geometry and `PublishedService.Tables` the tables, so the hundred-odd
  loops in WMS, WFS, KML, MapServer, the tile faces and the legend never see a table; and the places that cannot meet
  one read `RequireGeometryColumn()` / `RequireGeometryType()`, which throw with the layer's name rather than draw nothing.
  The feature face asks `LayerOrTable(index)`.
- **"`GeometryKind?.ToString()` answers `""`, and the compiler does not see it."** True, and it was found twice in this
  change — the catalogue insert would have written `''` as a geometry type. Both are fixed and the remaining call sites
  were read by hand; `ATableIsStoredReadAndEditedTests` reads the stored row back and asserts SQL null in both columns.
- **"A table's `srid` of 4326 is a sentinel."** It is a value nothing reads (§5c), kept so that a column every other row
  needs does not become a third nullable; the alternative was more churn for no different answer.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| ArcGIS lists a table as `{id, name}` in the service's `tables` array, numbered with the layers | `ServiceRequest/FeatureServer?f=json` → `layers: [{id: 0, …}]`, `tables: [{id: 1, name: "ServiceRequestComment"}]` | Esri sample server, measured 2026-10-21 |
| A table's document is `type: "Table"` with no `geometryType`, `extent`, `drawingInfo`, `minScale`/`maxScale`, `hasZ`/`hasM`, `allowGeometryUpdates` or `supportedSpatialRelationships` | `ServiceRequest/FeatureServer/1?f=json`, its key list compared one by one | same |
| A spatial filter on a table is ignored | `…/1/query` counts 8 with `geometry=<envelope>` and 8 without | same |
| `returnExtentOnly` on a table is a 400 | `{"error":{"code":400,"message":"Unable to complete operation.","details":["Query with extent request failed."]}}` | same |
| `returnGeometry=true` on a table answers attributes only, and the answer names no `geometryType` or `spatialReference` | response keys: `objectIdFieldName`, `globalIdFieldName`, `fields`, `features`, `exceededTransferLimit` | same |
| This server now answers the same shapes | registered table, gdb import (point class + table + empty table + attachment table), designed table, relationship layer→table, `queryRelatedRecords`, `applyEdits`, an attachment on a table row | local fixture `gistables`, probe scripts, 2026-10-21 |

## 5. Decision

**A published layer may be a table: a PostGIS table or view with an identity column and no geometry column, served on
the FeatureServer beside the service's layers as ArcGIS serves a standalone table.**

a. **The model.** `LayerDefinition.GeometryColumn` and `PublishedLayer.GeometryType` are null exactly together, and
   `IsTable` says so. `PublishedService` splits its layers into `Layers` (geometry) and `Tables`, with `LayersAndTables`
   for whatever is about the data — sharing, deletion, history, capabilities, exports — and `LayerOrTable(index)` for
   the feature face. Migration 99 drops the two `not null`s, checks they are null together, and adds a per-service
   uniqueness index for tables.

b. **The ArcGIS face.** The service document lists tables in `tables`; `/FeatureServer/layers` writes each table's own
   document there. A table's document is the layer document narrowed — `type: "Table"`, the geometry keys of §4 absent,
   a template whose drawing tool is `esriFeatureEditToolNone`, `supportsReturningQueryExtent` false. A query answers
   attributes only, with no `geometryType` or `spatialReference`; pbf says `esriGeometryTypeNone` (127). A spatial filter
   is ignored, as ArcGIS ignores it; `returnExtentOnly` is a 400; a renderer and a symbology are refused by name.

c. **No reference.** A table carries `srid` 4326 (`LayerDefinition.TableSrid`), which nothing reads: no extent is taken,
   no geometry is filtered or transformed.

d. **Writes.** `applyEdits` adds, updates and deletes rows as for a layer. A row sent **with** a geometry is refused with
   a sentence rather than stored without it — `INFERRED`: what ArcGIS does was not measured, because its sample server is
   not ours to write to. Attachments and history work on a table as on a hosted layer.

e. **How a table comes to exist.** `POST /admin/layers` without `geometryColumn` publishes a registered PostGIS table;
   `POST /admin/hosted/define` with `geometryType: "Table"` designs an empty one; a geodatabase class that OGR reports
   as `wkbNone` is imported as one, empty or not, with its field aliases and domains. A table published from any other
   engine or from a file is refused at publish: their sources read a geometry column on every request.

f. **A geodatabase's attachment tables** (`<class>__ATTACH`, or `<class>_ATTACH` as one of the owner's exported archives
   names them) **become their class's attachments**: read after every class, each file is stored on the feature it
   names — by `REL_GLOBALID` against the class's carried-over GlobalIDs, or by `REL_OBJECTID` translated into the order
   the rows were copied in, which is the new object id. A file naming a feature the class does not have is counted as
   `unmatched` and not stored. Before this, any such table failed Pro's whole publish.

g. **What draws, does not draw tables.** MapServer, VectorTileServer, WMS, WFS, KML, OGC API Features/Maps/Tiles and the
   thumbnails see `Layers` only. A service that holds only tables offers no vector tiles.

## Conditions

1. **The console shows tables** — the service's page lists them apart from its layers, the design form offers *Table*,
   the geodatabase picker ticks them (done in this change), and every screen that reads a layer's geometry type copes with
   none. Each screen passes the ux-designer, as every screen does.
   **DISCHARGED 2026-10-22**: the service page's Overview and Data tabs list a service's tables apart from its layers and
   open each one's rows; the design form offers *Table* and says what it makes; the geodatabase picker ticks a table and
   names an attachment table as *attachments of* its class; Settings, symbology, the visible range, tiles and the cover
   skip a table rather than asking it for a shape; and the map viewer says a service of tables alone has nothing to
   draw instead of drawing an empty map. Two ux-designer passes, the second on the Publish screen, both rendered on a
   fixture holding a mixed and a table-only service; the console suite 316 of 316 on a fresh fixture.
2. **A geodatabase's attachments are carried over as attachments** — `__ATTACH` rows onto the layer's features, matched by
   `REL_GLOBALID` or `REL_OBJECTID` — so Pro's publish of a layer with photos keeps them (§5f's skip ends).
   **DISCHARGED 2026-10-21**: the reader's `attachments` operation streams them, and `GeodatabaseImporter` stores them
   through the layer's own attachment store. Measured on a GDAL-written archive with both keyings, a deleted feature and
   an orphan: three files on two sites by GlobalID, two on two wells by position across the gap, one orphan in each
   counted and not stored, every byte read back through `/attachments/{id}`. What Esri's own writer produces is not yet
   read (condition 4 is where it is).
3. **A geodatabase's relationship classes are carried over as declared relationships**, read through GDAL's relationship
   API (a public interface, not the geodatabase's system tables — ADR-013 §3), so an inspection table arrives related.
   **DISCHARGED 2026-10-21**: the reader's `relationships` operation reports them, and the import declares each one whose
   two classes it published, on the server's own catalogue, named `<service>_<class>`. One-to-one and one-to-many are
   declared (many-to-one as its one-to-many, origin first); many-to-many, a multi-field key and a class that was not
   published are reported as not carried, and a composite class is declared simple with the cascade named as not carried
   (D-26). Measured on a GDAL-written composite one-to-many: declared, listed on both documents, and
   `queryRelatedRecords` answering each site's inspections; the reader's fixture carries one, which
   `GeodatabaseReadsCorrectlyTests` follows.
4. **ArcGIS Pro publishes a map with a standalone table and its relationship**, measured with the Pro harness, and the
   Maps SDK opens it (`FeatureLayer` + table, related records in a pop-up).
   **DISCHARGED 2026-10-21**, against a local fixture through the plain-HTTP twin and recording relay. *Read*: arcpy
   describes an imported service as a workspace of `FeatureClass`, `Table` and `RelationshipClass`
   (`R1L0sitesL1inspections`), reads the table's rows through a search cursor, makes a table view of it and exports it.
   *Publish*: arcpy's `getWebLayerSharingDraft` → `StageService` → `UploadServiceDefinition` of a map holding a point
   class with GlobalIDs and one attachment, a standalone table and a simple one-to-many relationship class, written by
   Pro's own geodatabase writer — the import reported 3 of 3 and the relationship declared; the service lists the table
   in `tables`, both documents name the relationship, and the attachment is on its feature. *SDK 4.29*: `isTable` true
   for the table, `geometryType` null, `map.tables` holds it, `queryFeatures` returns its rows without geometry,
   `queryRelatedFeatures` answers the site's two inspections, `queryAttachments` the note. Pro's UI (the Share pane,
   the attribute table, a pop-up's related list) was not driven; arcpy is the same publishing path.
5. **A registered source's probe lists its tables without geometry**, so the console can offer them; today only the API
   can publish one. *(The probe half done 2026-10-21: tables without a geometry or geography column are listed, never the
   platform store's schema, the recycle bin's, an extension's relations or the tables kept beside a layer; a composition
   carries a table at its top level and refuses one in a group, by the same source rule as a single publish. The
   Publish screen's half is open.)*
   **DISCHARGED 2026-10-22**: the Publish screen lists a table without geometry with a *table* mark in place of a shape
   and a reference, it drags into Contents like any layer, and a group refuses it with the server's own sentence. A table
   from a source whose reader cannot serve one says so on the row, by the same list of kinds as the server's refusal
   (PostGIS, SQL Server, MySQL; condition 7 widens both together). SQL Server's and MySQL's probes list theirs too.
6. **MapServer's document lists the service's tables**, as ArcGIS's MapServer does; today its `tables` is empty.
   **DISCHARGED 2026-10-21**: the service document and `/MapServer/layers` list them, `/MapServer/{id}` answers a table's
   document (`type: "Table"`, fields, `Query,Data`), and its query is the FeatureServer's handler. A service of tables only
   has no map service at any address — the services directory already listed none, and answering one sent the Map
   Viewer to add an empty map. Measured on the fixture: 404 for `ci_tables/MapServer` and `/MapServer/0`; three tables
   listed and `/MapServer/1` a table for the mixed service.
7. **Oracle, MySQL/MariaDB, SQL Server and DuckDB tables** — their sources read a geometry column unconditionally;
   until each answers without one, a table there is refused at publish (§5e).
   **DISCHARGED 2026-10-22** — all four engines; Oracle last, the same day, after the owner started its database again
   for it. *SQL Server*: its probe lists a table
   with no geometry or geography column and a single whole-number primary key, and its source and writer serve it — rows
   without shapes whatever filter is sent, no extent, attributes edited, a shape refused (`SqlServerTableTests`, LocalDB).
   *MySQL/MariaDB*: the same, measured against MariaDB (`MySqlTableTests`); a history is not kept for a table there,
   because that history keeps each version's shape in an indexed spatial column, and enabling one says so. *DuckDB*: a
   table in a DuckDB or MotherDuck database with no GEOMETRY column, and a Parquet file with no `geo` key and no GEOMETRY
   column, are listed as tables; a byte column named as geometry is (`geom`, `wkb_geometry`, …) still refused with the
   sentence that tells its owner how to cast it, because that is a layer somebody meant. A database file's table is
   edited with its attributes; a Parquet file's is read-only, as every file layer is (`DuckDbTableTests`). Measured end
   to end on a local fixture: each registered, probed, published, its document `type: "Table"`, a query with an envelope
   answering every row, `returnExtentOnly` refused, and the database file's table added to, updated, deleted from, and
   refusing a shape. The server's list of kinds that publish a table (`TablesServedBy`) and the Publish screen's carry
   all four. *Oracle*: first written up as not done, because its database had been stopped since 2026-10-20 at the
   owner's request and nothing could be measured; the owner started it again on 2026-10-22 for this. Its probe lists a
   table with no SDO_GEOMETRY column and a single whole-number primary key, only in schemas Oracle does not maintain
   itself, and never this server's `__ATTACH` and `__HISTORY` tables, a spatial index's `MDRT_`/`MDXT_` tables or the
   recycle bin. Its source reads a table without asking the spatial metadata, which has no row for it, and answers every
   row without a shape; its writer reads the key's facts from the dictionary alone and refuses a shape; a history is not
   kept for a table there, for the reason MySQL's is not (`OracleTableTests`, against TRDATA on Oracle 26ai Free; the
   provider's suite 105 of 105). Measured end to end on a local fixture: registered, the probe offering six layers and
   nine tables — every one of them a test table of the schema's own — published, `type: "Table"`, a query with an
   envelope answering both rows, a row added, updated and deleted, and a shape refused.
8. **OGC API Features serves a table as a collection whose features have a null geometry** (Part 1 allows it); today a
   table is not a collection there.
   **DISCHARGED 2026-10-21**: a table is a collection with no spatial extent (its temporal one stays), its items have
   `geometry: null`, and a `bbox` selects none of them — the rule `OgcExtentConformanceTests` already holds this face to,
   and the opposite of the ArcGIS face's, which ignores a spatial filter on a table as ArcGIS does. Measured: four items
   without a bbox, none with the world as one.
9. **A view of a service (ADR-113) carries its tables**; today `AdminEndpoints.Views` builds views of its layers only.
   **DISCHARGED 2026-10-21**: a view is made of the source's hosted layers and tables, and lists, filters and remakes
   either. Measured: a view of a point layer and three tables lists one layer and three tables. Relationships do not come
   with a view, for a layer or a table — that is ADR-113's, not this decision's.
10. **The migration tool carries an ArcGIS service's tables** — `InventoryScan` reports them as staying behind and
    `MigrationPlan` matches layers with a geometry column only.
    **DISCHARGED 2026-10-21**: the plan reads a service's `tables` beside its `layers`, matches a table to a table without
    a geometry column, writes `kind: "table"`, and `apply` publishes it with no geometry; the inventory says a table
    comes. Measured with this server as the source: `ci_tables`'s table matched `public.denetimler` and was published
    into a new service's `tables`.

## 6. Consequences

**Positive.** The inspection model — a point layer, a related table, photos — exists. A Pro map holding a table or an
attachment table no longer fails to publish. A table gets query, statistics, editing, attachments, history,
relationships and sharing on the day it exists, because it shares their code.

**State.** In the catalogue: a `layer` row with `geometry_column` and `geometry_type` null (migration 99), in the same
service and numbering as the layers. At runtime nothing new: a table's description is cached by `ServiceContexts` as a
layer's is, per node. The table itself is the publisher's — hosted in the datastore, or theirs in a registered source.

**Negative.** Two members every face reads are nullable, guarded by a split and two throwing accessors rather than by the
type system alone. A table carries a reference it does not have (§5c). Attachments in a geodatabase are still left
behind (condition 2). Only PostGIS publishes a table (condition 7).

## 7. Assumptions this decision rests on

- A client tells a table from a layer by `type` and by the absence of `geometryType`, as ArcGIS's own documents allow it
  to. Measured on the documents, not on every client.

## 8. Dependencies

ADR-013 (ArcGIS compatibility), ADR-038 (geodatabase import), ADR-064 (editor tracking), ADR-078 (history),
ADR-191 (Pro publishing), ADR-207 (attachments on registered layers).

## 9. Revisit triggers

- A client that reads `geometryType: null` and fails where a missing key would not.
- A second engine asked to publish tables (condition 7).

## 10. Dissent

None recorded. The veteran's review argued for this first; the owner decided it the same day.
