# ADR-240 — grapy holds editor tracking, last edit time, COGO, unregistered tables and registration, and refuses enterprise geodatabase administration by name

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-31, by owner decision on scope: the `arcpy.management` names refused as *"needs a data model this server does not hold yet"* may no longer be refused for that reason. Each is built, built as an equivalent, or refused with a reason of its own that is not a schedule. How a GeoPackage holds what an enterprise geodatabase holds is INFERRED and listed for confirmation in §5.6 and [Q-208](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) put the geodatabase models into group 2, and
`grapy.management` refused 37 names with a schedule: *"needs a data model this server does not hold yet (ADR-224
group 2)"*, grouped under five labels — enterprise geodatabase administration (24 names, of which two, the database
sequences, ADR-235 had since built), parcel fabrics (EnableCOGO, DisableCOGO), geodatabase editing behaviours (eight),
trajectory datasets (four) and annotation feature classes (AppendAnnotation). The owner ruled on 2026-10-31 that a
schedule is not a reason. A toolbox that turns on editor tracking for the class it builds, creates a view, or updates
statistics after a load stopped on a name that a GeoPackage can answer.

Half of these names really are administration of a DBMS server or of Esri's system tables. The other half name
behaviours whose fields and effects the pages document, on a store that grapy holds: the GeoPackage.

## 2. Alternatives considered

### Alternative A — build what a GeoPackage can answer, and refuse the rest one name at a time (chosen)

Editor tracking, last edit time, COGO fields, incrementing ID fields, unregistered tables, registration, views and
statistics have documented semantics that SQLite and the GeoPackage standard can carry. Server administration,
Esri's system tables, portal ownership, client drawing behaviours and trajectory ingestion cannot. **For:** each name
answers what its page says, or says why it cannot. **Against:** *registered*, *database-maintained* and *delta tables*
are an enterprise geodatabase's words; mapping them to a GeoPackage is a reading, not a reproduction (§3, §5.6).

### Alternative B — refuse every enterprise-administration page, since the pages say *enterprise geodatabase*

Analyze, AnalyzeDatasets and RebuildIndexes say they work on an enterprise geodatabase or database;
CreateUnRegisteredTable and AddIncrementingIDField on an enterprise database. **For:** literal. **Against:**
CreateDatabaseView's and CreateUnRegisteredFeatureclass's pages list GeoPackage and SQLite themselves, and a
GeoPackage *is* a database. Updating SQLite's statistics after a load is what a toolbox calling Analyze wants.

### Alternative C — store the enterprise settings without effect (feature binning, split model, DBTUNE keywords)

**For:** the calls succeed. **Against:** a setting nothing reads is silently wrong (ADR-224 §5.4, ADR-235 §2 C).
Rejected; those names are refused.

## 3. Counterarguments to the preferred option

A GeoPackage has no geodatabase registration, so *unregistered* here means "not in `gpkg_contents`", which every
GeoPackage reader agrees on but Esri's pages never say. A table with an integer key in `gpkg_contents` already has a
database-maintained ID, so AddIncrementingIDField only works on the unregistered tables grapy now makes — literal to
the page (*"cannot run on tables registered with the geodatabase"*), but narrow. Editor tracking on insert stamps the
last editor too; the help topic says values are recorded when a row is *"inserted or modified in any way"* but does
not say so of the editor fields outright, and Pro may differ. The last edit time lands in `last_change`; an ArcGIS
feature service reads its own system table, not the GeoPackage's.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| EnableEditorTracking's seven parameters; text creator/editor fields, low-precision date fields; ADD_FIELDS; UTC or DATABASE_TIME; re-running may add fields but not move them or change the time standard; attachment tables tracked with their class; feature dataset default names `created_user`, `created_date`, `last_edited_user`, `last_edited_date` | Tool page, read 2026-10-31 | doc.esri.com …/data-management/enable-editor-tracking |
| Creation records creator and date; values recorded when a row is inserted or modified; earlier rows stay null; a file or mobile geodatabase records the operating system login; UTC required for branch versioned data | Help topic | …/help/data/geodatabases/overview/introduction-to-editor-tracking |
| DisableEditorTracking's four DISABLE_ keywords, all on by default | Tool page | …/disable-editor-tracking |
| Describe's `editorTrackingEnabled`, `creatorFieldName`, `createdAtFieldName`, `editorFieldName`, `editedAtFieldName`, `isTimeInUTC` | Editor tracking properties page | …/arcpy/functions/editor-tracking-properties |
| EnableLastEditTime: nonversioned only; a feature dataset passes it to classes added later | Tool page | …/enable-last-edit-time |
| `gpkg_contents.last_change` is the timestamp of last change to a table's content | OGC GeoPackage Encoding Standard 12-128r18, §1.1.3.1.1 | opengeospatial.org |
| EnableCOGO adds five double fields (Direction, Distance, Arc Length, Radius, Radius2) to a line class, only the missing ones; Direction north azimuth in decimal degrees; DisableCOGO leaves fields that "can be deleted" | Tool page | …/enable-cogo |
| AddIncrementingIDField(in_table, {field_name}) default `ObjectID`; refused on registered tables; a name taken stops it; no second ID field | Tool page | …/add-incrementing-id-field |
| CreateUnRegisteredTable / CreateUnRegisteredFeatureclass: `OBJECTID` integer or the template's fields; GeoPackage and SQLite listed; geometry type, has_m, has_z, spatial_reference, config_keyword | Tool pages | …/create-unregistered-table, …/create-unregistered-feature-class |
| RegisterWithGeodatabase: object ID field integer or created and populated; geometry type required with a shape field; reference needed only for an empty table; extent; views read-only; file, mobile and enterprise geodatabases | Tool page | …/register-with-geodatabase |
| CreateDatabaseView: GeoPackage and mobile geodatabase listed; the database validates the SQL; views not registered | Tool page | …/create-database-view |
| Analyze's components BUSINESS, FEATURE, RASTER, ADDS, DELETES; AnalyzeDatasets' and RebuildIndexes' parameters; ONLY_DELTAS the default; a dataset needed without SYSTEM | Tool pages | …/analyze, …/analyze-datasets, …/rebuild-indexes |
| Feature binning: bin types and statistics; levels named WORLD … CITY; no bin size per level given; cache storage not given | Tool pages | …/enable-feature-binning, …/manage-feature-bin-cache |
| SetFeatureClassSplitModel governs only the split edit operation | Tool page | …/set-feature-class-split-model |
| Trajectory datasets: a container of footprints, metadata and file references, no tables defined; AddData reads Sentinel-3 SRAL, Sentinel-6, CryoSat-2, ICESat-2 netCDF/HDF through trajectory types whose footprints are not defined | Tool pages; *Trajectory type properties* | …/create-trajectory-dataset, …/add-data-to-trajectory-dataset, …/help/data/imagery/trajectory-type-properties |
| An annotation feature keeps its symbol and text geometry in the Element BLOB and points (SymbolID) into the class's symbol collection | Pro help (*Managing annotation feature class properties*), Pro SDK *ProConcepts Annotation* | doc.esri.com …/help/data/annotation/managing-annotation-feature-class-properties; …/sdk/…/ProConcepts-Annotation |

No benchmark is at stake: these are schema and metadata operations on the file a tool works on.

## 5. Decision

1. **Editor tracking** (`grapy/_behaviours.py`, `grapy/_gdbtools.py`). EnableEditorTracking and DisableEditorTracking
   keep a class's four field names and its time standard as a row of `grapy_editor_tracking`. Every insert and update
   through `arcpy.da.InsertCursor` and `UpdateCursor` — after ADR-235's attribute rules, so a rule cannot overwrite a
   stamp — and every row an attribute rule's `edit` dictionary adds or updates, and every attachment AddAttachments
   writes, is stamped: an insert all four fields, an update the last editor and date. Branch versioned and archived
   classes (ADR-227) are stamped on every version's edits. Describe answers the six documented properties.
2. **Last edit time.** EnableLastEditTime puts three SQLite triggers on the class that set its
   `gpkg_contents.last_change` on every insert, update and delete, by any writer of the file. A versioned or archived
   class is refused, as documented; a feature dataset passes it to classes created in it later.
3. **COGO.** EnableCOGO adds the five COGO double fields to a line class and marks it (`grapy_cogo`); DisableCOGO
   unmarks it and keeps the fields.
4. **Unregistered tables, incrementing IDs, registration, views and statistics.** CreateUnRegisteredTable and
   CreateUnRegisteredFeatureclass write plain SQLite tables absent from `gpkg_contents`; AddIncrementingIDField adds a
   trigger-kept ID to one; RegisterWithGeodatabase lists one in `gpkg_contents` (and `gpkg_geometry_columns`) with its
   object ID as the integer primary key; CreateDatabaseView writes a SQLite view; Analyze and AnalyzeDatasets run
   SQLite's `ANALYZE`, RebuildIndexes its `REINDEX`. All are **built**, not equivalents: each does what its page says
   on the store grapy holds; what a GeoPackage makes of the enterprise words is §5.6.
5. **Refused, each with its own reason** (`_gdbtools.REFUSED`): CreateEnterpriseGeodatabase,
   EnableEnterpriseGeodatabase (DBMS server connection; Esri's unpublished system tables), UpdateEnterpriseGeodatabaseLicense
   (ArcGIS Server keycodes), UpgradeGDB (no geodatabase release in a GeoPackage), CreateDatabaseUser (no users in a file;
   no server connection), CreateRole (no roles), ChangePrivileges (SQLite has no GRANT), CreateSpatialType (ST_Geometry
   in Oracle or PostgreSQL), ConfigureGeodatabaseLogFileTables (selection log tables grapy does not write),
   ExportGeodatabaseConfigurationKeywords and ImportGeodatabaseConfigurationKeywords (no DBTUNE table; no storage
   parameters), DeleteSchemaGeodatabase (Oracle user schemas), MigrateStorage (one geometry storage), UpdatePortalDatasetOwner
   (a portal, no network, ADR-223 §5.3), EnableFeatureBinning, DisableFeatureBinning and ManageFeatureBinCache (bin sizes
   per level not defined, and no reader of a bin in arcpy), SetFeatureClassSplitModel (grapy has no split edit, as
   SetRelationshipClassSplitPolicy is already refused), CreateTrajectoryDataset, AddDataToTrajectoryDataset,
   MakeTrajectoryLayer and RepairTrajectoryDatasetPaths (tables and footprints undefined), and AppendAnnotation —
   with `arcpy.Annotation`, changed to the same reason: the Element BLOB and the symbol collection are Esri's
   unpublished binary.
6. **INFERRED, for the owner's confirmation** ([Q-208](../open-questions.md)):
   - (a) storage: `grapy_editor_tracking`, `grapy_last_edit_time`, `grapy_cogo`, `grapy_incrementing_id` and
     `grapy_unregistered`, registered as the `grapy_edit_behaviours` extension; a feature dataset's last edit time a
     row named `<dataset>/`;
   - (b) editor tracking: with ADD_FIELDS and no field named, the four default names; an insert stamps all four fields;
     a value an edit gives a tracked field is replaced; the user is `getpass.getuser()`, empty where there is none;
     DATABASE_TIME is the writing machine's local time; dates are stored to the second as the GeoPackage text grapy
     writes; with NO_ADD_FIELDS an attachment table is tracked only on the fields it has; DisableEditorTracking keeps the
     fields and values;
   - (c) last edit time is `gpkg_contents.last_change`, kept by triggers; a table not in `gpkg_contents` is refused;
   - (d) the arc length field is `ArcLength`; a COGO field there with another type is an error; the layer symbology
     and label expression Pro adds are a map's and are not written;
   - (e) *registered* means listed in `gpkg_contents`; an unregistered class's geometry column is `geom` declared with
     the GeoPackage type name, its reference and z/m kept in `grapy_unregistered` until registration; the template's
     object ID becomes a plain integer field; without `spatial_reference`, the first template's;
   - (f) an incrementing ID is an integer column, numbered in rowid order, kept by an AFTER INSERT trigger and a unique
     index;
   - (g) registration rebuilds the table with the object ID as `integer primary key autoincrement`; with no field named,
     an `OBJECTID` there is used and its nulls numbered after its largest value, else a new `OBJECTID` is made;
     repeated values are an error; shapes written with reference 0 take the class's in their header; z and m are on if
     any shape or the creation said so; a view and a raster are refused by value;
   - (h) BUSINESS and FEATURE are the class's one table; ADDS, DELETES, `analyze_delta`, `analyze_archive` and
     RebuildIndexes' deltas are its ADR-227 `__history` table; SYSTEM is every `gpkg_*` and `grapy_*` table; RASTER finds
     no raster table in a GeoPackage class;
   - (i) a view's object ID is its first integer field, and a cursor's `*` on a table or view with no shape column
     leaves SHAPE@ out.

## 6. Consequences

- **State.** Up to five grapy tables in the GeoPackage the tool works on, triggers on the classes that keep last edit
  time or an incrementing ID, and nothing in the catalogue or on the server.
- Against `grapy-surface` at 60d461f2, `arcpy.management` moves from 227 to 241 built (equivalents 20, unchanged) and
  from 220 to 206 refused; all modules from 1,171 to 1,185 of 2,174. No `arcpy.management` name is refused with a
  schedule by this ADR's names any more.
- **Negative.** A writer that is not grapy fills no editor tracking fields (the triggers of §5.2 and §5.4 do work for
  it). Registering rewrites the whole table once. A GeoPackage reader sees `grapy_*` tables it does not understand,
  as it already does for ADR-225 … 235.
- **Ports created.** None; standard library `sqlite3` only.

## 7. Assumptions this decision rests on

- The GeoPackage standard's `gpkg_contents` is what every reader treats as the list of a file's datasets, so it is the
  nearest reading of *registered with the geodatabase*.
- The help topic's *"inserted or modified in any way"* covers the editor fields on insert.

## 8. Dependencies

[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) §5.3 (no network or server connection),
[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md),
[ADR-227](ADR-227-grapy-holds-versions-and-archives.md) (history tables; versioned classes),
[ADR-228](ADR-228-grapy-holds-subtypes-and-attachments.md) (attachment tables),
[ADR-235](ADR-235-grapy-holds-attribute-rules-contingent-values-and-replicas.md) (the cursor wiring this follows).
Clean room (CLAUDE.md §5): built from the public pages in §4; no Esri implementation read.

## 9. Revisit triggers

- Esri publishes the annotation Element format, the trajectory dataset tables, or the bin sizes per level of detail.
- grapy gains a split edit (SetFeatureClassSplitModel and SetRelationshipClassSplitPolicy become buildable together).
- A toolbox depends on an INFERRED choice of §5.6 answering differently from Pro.

## 10. Dissent

None recorded.

## 11. Conditions

1. **Editor tracking on insert compared with ArcGIS Pro** on a file geodatabase: whether an insert fills the last
   editor and last edit date fields (§5.6 b). Needs a Pro session; until then the reading of §3 stands.
2. **The owner answers [Q-208](../open-questions.md).**
