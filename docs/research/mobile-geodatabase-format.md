# The mobile geodatabase, as ArcGIS clients read it for offline sync

Written 2026-11-10 for [ADR-288](../adr/ADR-288-offline-sync-is-in.md). What a server has to write for ArcGIS Field
Maps and ArcGIS Pro to take a feature service offline, and what it has to read when they bring edits back.

## 0. How this was learned — the disclosure ADR-030 and ADR-286 require

**Esri does not publish this format.** Its public documentation says the mobile geodatabase is SQLite, documents SQL
access through its own ST_Geometry extension, and documents the REST operations that move one (`createReplica`,
`synchronizeReplica`, *Using sync*). The tables, the item definitions and the geometry blob are not in it.

Everything below was therefore **observed**, by owner decision of 2026-11-10 (*"Gözleyerek türet"*, chosen from three
options; ADR-288 §5). The sources, all four of them:

| Source | What it gave | Whose data |
|---|---|---|
| ArcGIS Pro 3.x's `arcpy`, writing geometries **we chose** into a mobile geodatabase it created (`CreateMobileGDB`, `InsertCursor`) | The geometry blob, by comparing known input with the bytes written — 50 geometries, every type, Z and M, parts, holes | Ours |
| ArcGIS Server 12.1 on Aecon's production portal, a hosted service **we created** and filled (owner's permission, 2026-11-10: *"test verisi açacaksak esri.aecon.com'u da kullanabilirsin"*): `createReplica` with `syncModel=none` and `perLayer`, then `synchronizeReplica` downloads after a server-side insert, update, delete and attachment | The snapshot file, the registered replica file and two delta files | Ours |
| Esri's public sample server (`sampleserver6`, `Sync/SaveTheBaySync`), `createReplica` with `syncModel=none` | A first look at the table set, before the Aecon service existed; nothing below rests on it alone | Esri's public sample data — **structure read, no content kept** |
| [FGDB Spec](https://github.com/rouault/dump_gdbtable/wiki/FGDB-Spec), Even Rouault's public reverse-engineered description of the File Geodatabase | That coordinates are integers on a grid, stored as signed variable-length integers delta-coded from the previous point. The exact bit layout below was **measured here**, not taken from it — the spec's revisions describe the sign bit differently | Public |

**Nothing was copied into the repository from any of these files.** The statements that create tables are written
here from what was read; the test vectors are geometries this project chose, with the bytes Pro wrote for them. The
captured files themselves stay outside the repository.

A finding that is a **measurement** is marked *(measured)*; one that is a reading of a single file and could be an
accident of that file is marked *(one sample)*.

## 1. The file

- A SQLite 3 database, UTF-8, page size 4096, `application_id` 1165193833 (`0x45737269`, ASCII *Esri*). *(measured, all files)*
- `GDB_Version` holds one row; ArcGIS Server 12.1 writes `12, 1, 0, 'ArcGIS Server 12.1.0-final - 12.1.0.786', 109504, 93001`,
  the 10.9.1 sample server `10, 9, 1, …, 109102, 93001`. Both are read by Pro 3.x. *(measured)*
- One collation, `ICUFOLDCASE`, is used by an index on `GDB_Items.Name`. A reader without it can still read every
  table; it cannot use that index. *(measured)*
- **Two virtual tables need modules only Esri's library has**: `st_spatial_reference_systems USING VSRS()` and
  `st_vtspindex_interface USING VTSpindex()`. Plain SQLite cannot execute either `CREATE VIRTUAL TABLE`. Written as
  rows of `sqlite_master` (with `writable_schema`), Pro opens, reads and edits the file. *(measured, §6)*
- The spatial index is ordinary SQLite R*Tree: `st_spindex__<table>_<column> USING RTREE (pkid, minx, maxx, miny, maxy)`,
  kept by triggers that call Esri's `InsertIndexEntry` and its siblings. Those functions exist on the device; a writer
  loads rows first and creates the triggers last. *(measured)*

## 2. The geometry blob *(measured — 50 of 50 blobs re-encode byte for byte)*

```text
int32 LE   (srid << 8) | 0x64
int32 LE   number of points, part separators included
byte       type     point 0x04 · multipoint 0x08 · polyline 0x14 · polygon 0x18;  + 2 with Z, + 1 with M
byte       flags    low nibble: 0x1 point or multipoint, 0x4 polyline, 0x8 polygon
                    0x40: more than one part (polyline, polygon) or more than one point (multipoint)
[only when the point count is above 10]
           xmin, ymin, xmax, ymax         signed varints, absolute, on the grid
[only when 0x40 is set and the shape is not a multipoint]
           extra part count, then each extra part's first point index, delta-coded    signed varints
4 bytes    payload length in bytes: a signed varint, zero-padded to four bytes
4 bytes    01, dimension byte (Z 0x01, M 0x02; polygons add 0x10), 00, then 04 for a polygon, else 00
payload    x0 y0 dx dy dx dy …   then z0 dz …   then m0 dm …         signed varints
```

- **The grid.** A coordinate is stored as `round((x − falseX) × xyUnits)`, with the false origin and units of the
  layer's spatial reference — `−400, −400, 10⁹` for WGS 84, `−20037700, −30241100, 10⁴` for Web Mercator; Z and M as
  `round((z + 100000) × 10000)` by default. The grid is declared per feature class, in the `SpatialReference` of its
  `DEFeatureClassInfo` (`XOrigin`, `YOrigin`, `XYScale`, `ZOrigin`, `ZScale`, `MOrigin`, `MScale`), so a writer
  chooses it and says so. *(measured)*
- **The signed varint.** First byte: bit 7 *more follows*, bit 6 *negative*, bits 0–5 the lowest six bits of the
  magnitude; each further byte: bit 7 *more follows*, bits 0–6 the next seven. *(measured)*
- **Parts.** Polyline paths and polygon outer rings are separated by one extra point at grid `(−1, 0)`, which the
  point count includes. **Holes follow their outer ring with no separator**; a reader finds the end of a ring where it
  meets its first point again. *(measured)*
- **Winding is OGC's, the reverse of ArcGIS JSON's**: a shell is stored counter-clockwise and a hole clockwise. Pro,
  given a clockwise shell and a counter-clockwise hole — ArcGIS JSON's convention — wrote both reversed. *(measured)*
- **Z and M** are written after all the XY pairs, each delta-coded on its own. *(measured)*
- **True curves were not sampled.** A layer with them is refused for offline (ADR-288 §5).

## 3. The tables a file holds

| Table | What it is | Written by the server as |
|---|---|---|
| `GDB_Items`, `GDB_ItemTypes`, `GDB_ItemRelationships`, `GDB_ItemRelationshipTypes` | The catalogue: one item per workspace, domain, dataset and (registered) replica, typed by fixed UUIDs, each with an XML definition | Item types and relationship types are fixed rows; items generated per layer |
| `GDB_TableRegistry`, `GDB_ColumnRegistry`, `GDB_Layers`, `GDB_RowidGenerators`, `GDB_Tables_Last_Modified`, `GDB_TablesModified` | Registration of each table, its columns, its layer extent, and the next object id | Generated per layer |
| `GDB_ServiceItems` | **Each layer's REST JSON, as the service answers it**, plus the service's own | The layer documents this server already writes |
| `GDB_EditingTemplates`, `…Relationships` | Feature templates (12.1 files only) | Empty is read |
| `GDB_ReplicaLog`, `GDB_Metadata`, `GDB_Locators` | — | Empty |
| `st_geometry_columns`, `st_aux_spatial_reference_systems` | The geometry column of each table, its type code (1 point, 4 multipoint, 5 polyline, 6 polygon; + 1000 Z, + 2000 M), dimension and SRID | Generated per layer |
| `<layer>`, `<layer>__ATTACH` | The rows, and attachments as `DATA` blobs keyed by `REL_GLOBALID` | Rows |

Item definitions are the geodatabase's XML (`DEWorkspace`, `DEFeatureClassInfo`, `DETableInfo`, `GPCodedValueDomain2`,
`GPRangeDomain2`, `GPFieldInfoEx`), the schema family Esri publishes for XML workspace documents. *(measured)*

## 4. A registered replica *(measured, one service)*

`createReplica` with `syncModel=perLayer` returns the same file with, in addition:

- a `GDB_Items` row of type **Sync Replica** (`GPSyncReplica`: name, id, GUID, the server generation of each layer)
  and one of type **Sync Dataset** (`GPSyncDataset`) per table and attachment table;
- **archiving on every table**: two columns, `gdb_from_date` and `gdb_to_date` (Julian days), a view `<table>_evw`
  (and `_evw2`) selecting the open rows, and `INSTEAD OF` triggers that edit through it. An open row ends at
  9999-12-31. An update closes the row (its `gdb_to_date` becomes the edit time) and inserts the new version; a delete
  closes it; an insert opens one. *(measured: Pro's `arcpy.da.Editor` against a rebuilt file, §6)*
- `GDB_TriggerWork`, empty.

The replica name may not contain a hyphen — `Replica name contains invalid character(s)`, from the server's log; the
REST error says only *Error in performing CreateReplica operation*. *(measured)*

## 5. A delta *(measured, two downloads)*

`synchronizeReplica` with `syncDirection=download`, `dataFormat=sqlite` returns a small geodatabase holding only the
change:

- `GDB_DataChanges`: one row — the replica's GUID, and the **new server generation** as text.
- `GDB_DataChangesDatasets`: one row per changed table — name, type (1 table, 2 feature class, 7 attachment table),
  `LayerId`, generation.
- `GDB_DataChangesDeltas`: one row per change table — the registration id of the table that holds the rows, the
  change type (**1 insert, 2 update, 3 delete**), and the dataset.
- `T_<n>_Inserts`, `T_<n>_Updates` with whole rows in the layer's own columns; `T_<n>_Deletes` with `OID` and
  `globalid` only.
- **`RecordsetClassID` is the object id of the change table's own item in `GDB_Items`** (`main.T_<n>_<Kind>`), not
  its registration id: the two downloads number their change-table items 4–7 and register the same tables as 14–17,
  and the deltas row names 4–7. `<n>` is the `GDB_DataChangesDatasets` row the change belongs to. *(measured, two
  downloads)*
- A delta is a whole geodatabase of its own: the system tables without `GDB_ServiceItems` and `GDB_TriggerWork`, the
  five `GDB_DataChanges…` tables registered as 9–13, and each change table registered and catalogued as a table or
  feature class — a feature class change table has its own R*Tree, `st_geometry_columns` and `GDB_Layers` rows. Change
  tables have no `GDB_ColumnRegistry` rows, and their row id generators start at 1 whatever they hold. *(measured, two
  downloads)*

What the device uploads to `synchronizeReplica` was not captured: a server that answers `createReplica` receives it
from Field Maps directly, which is the measurement ADR-288's first condition asks for.

## 5a. What a map needs before Field Maps offers to take it offline *(one portal, read only)*

Read on Aecon's production portal (Enterprise 12.1) on 2026-11-10, item metadata only:

- **An offline-enabled web map carries the type keyword `Offline`** — Map Viewer's *Enable offline* switch. Its data
  has no `applicationProperties.offline`; the keyword is the whole mark. Two of two maps read.
- Its basemap is a tiled service a client can export from (`ArcGISTiledMapServiceLayer`). Graticula's vector tile
  services can be (ADR-098).
- **A preplanned map area is an item of type `Map Area`** (type keywords `Map`, `Map Area`), related to its web map by
  `Map2Area`, whose data holds `mapAreas` (`mapAreaTileScale`, `mapAreaRefreshParams`, `mapAreasScheduledUpdatesEnabled`)
  and whose `properties` hold the area's `extent` and a `status`. It is related by `Area2Package` to **one package per
  source**: an item of type `SQLite Geodatabase` per feature service, with type keywords `Bidirectional`, `Data`,
  `MapAreaPackage`, `Package`, `ReferenceURL#<the FeatureServer address>`, `Replica`, `SQLite Geodatabase`, `Syncable`;
  and a `Tile Package` (`tpk`) for the basemap, `ReferenceURL#` its source. What Field Maps calls to register such a
  package on first sync is not yet measured (ADR-288 condition 3).

## 6. The test that matters: a file written without Esri's library

A probe rebuilt two of the captured files using plain SQLite only — schema statements replayed, the two Esri-module
virtual tables written into `sqlite_master`, triggers last, **every geometry re-encoded by our encoder** — and ArcGIS
Pro read every table and geometry and then **inserted, updated and deleted** through its own editor, archiving
included. That is the evidence that a server can write this format; it is not yet evidence that Field Maps accepts it.

**The production writer, 2026-11-10.** `Graticula.Offline.Sqlite`, written fresh in C# from `writer_proto.py`, wrote a
four-layer fixture (points with Z and attachments, lines, an area with a hole, a table, a coded and a range domain) as
a snapshot, a registered replica and a download delta. Pro listed every table of all three and read every geometry,
and through `arcpy.da.Editor` inserted, updated and deleted in the registered one: the update closed the old row and
opened a new one, the delete closed its row, the insert took its object id from the generator the writer set. The
same writer's reader read a download delta ArcGIS Server wrote back into its rows, attachment and table insert.
*(measured)*
