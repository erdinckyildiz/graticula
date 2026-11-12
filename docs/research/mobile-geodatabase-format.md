# The mobile geodatabase, as ArcGIS clients read it for offline sync

Written 2026-11-10 for [ADR-288](../adr/ADR-288-offline-sync-is-in.md). What a server has to write for ArcGIS Field
Maps and ArcGIS Pro to take a feature service offline, and what it has to read when they bring edits back.

## 0. How this was learned — the disclosure ADR-030 and ADR-286 require

**Esri does not publish this format.** Its public documentation says the mobile geodatabase is SQLite, documents SQL
access through its own ST_Geometry extension, and documents the REST operations that move one (`createReplica`,
`synchronizeReplica`, *Using sync*). The tables, the item definitions and the geometry blob are not in it.

Everything below was therefore **observed**, by owner decision of 2026-11-10 (*"Gözleyerek türet"*, chosen from three
options; ADR-288 §5). The sources, all five of them:

| Source | What it gave | Whose data |
|---|---|---|
| ArcGIS Pro 3.x's `arcpy`, writing geometries **we chose** into a mobile geodatabase it created (`CreateMobileGDB`, `InsertCursor`) | The geometry blob, by comparing known input with the bytes written — 50 geometries, every type, Z and M, parts, holes | Ours |
| ArcGIS Server 12.1 on Aecon's production portal, a hosted service **we created** and filled (owner's permission, 2026-11-10: *"test verisi açacaksak esri.aecon.com'u da kullanabilirsin"*): `createReplica` with `syncModel=none` and `perLayer`, then `synchronizeReplica` downloads after a server-side insert, update, delete and attachment | The snapshot file, the registered replica file and two delta files | Ours |
| Esri's public sample server (`sampleserver6`, `Sync/SaveTheBaySync`), `createReplica` with `syncModel=none` | A first look at the table set, before the Aecon service existed; nothing below rests on it alone | Esri's public sample data — **structure read, no content kept** |
| A preplanned map area's package on the same portal, which another Aecon user set up in 2024, read 2026-11-12 under the same permission (§5a) | The package item's `properties`, and the file's system tables and replica definitions | Aecon's — **system tables read, no rows read, nothing kept in the repository** |
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

## 5b. What Field Maps uploads *(measured 2026-11-10, one upload)*

ArcGIS Field Maps 26.2.2 on Android, after an offline insert, update and delete on a replica this server wrote, uploaded
through the FeatureServer's `uploads/upload` a 196 KB mobile geodatabase, kept on dev with
`Graticula:Offline:KeepUploadsIn`. It has the download delta's shape (§5) with these differences:

- `GDB_Version` says `10, 2, 2, '10.2.2 geodatabase'` — the client library's, not the server's.
- The change tables are named in lower case — `T_1_inserts`, `T_1_updates`, `T_1_deletes` — where the server's download
  wrote `T_1_Inserts`. A reader matches them without regard to case.
- `GDB_DataChanges.ServerGen` is `'0'`; the layer's generation is on its `GDB_DataChangesDatasets` row.
- `GDB_DataChangesEditResults` is present and empty.
- Inserts and updates carry the layer's own columns only, with the geometry; deletes carry `objectid` and `globalid`.
  `GDB_DataChangesDeltas` names them 1, 2, 3 as the download does.
- **The geometry of the change tables is written in SRID 300001**, a client-local reference that
  `st_aux_spatial_reference_systems` defines as EPSG 4326 with the grid `−400, −400, 10⁹` — the layer's own. A reader that
  decodes with the layer's grid read it right here; one that meets a client-local SRID whose grid differs from the layer's
  must take the grid from that row.
- Its new rows carry the object id the client gave them (7), and the server kept the GlobalID.

Applied through `applyEdits` by GlobalID, all three arrived: the insert at its position with Z 15, the update's new value,
the delete.

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

**A package, read 2026-11-12** (one map area on the same portal, packaged by ArcGIS Server 11.2 in 2024; system tables
only, no row read):

- **The package is a replica registered on the feature service when the area is packaged.** It is `perLayer` and
  bidirectional, its name is `SDS_FS_<milliseconds>`, and its owner is the account that set up the area. The server's
  `replicas/{id}` still answers for it, with a `lastSyncDate` two years after its creation. So one registered replica
  stands behind every copy of the package, and refreshes or devices sync it.
- **The package item's `properties` describe the replica:** `url`, `layers`, `extent`, `layerQueries`, `replicaID`,
  `layerServerGens` (`[{serverGen, id}]`), `returnAttachments`, `attachmentsSyncDirection`, `syncModel`,
  `useSyncForRefresh`, `hasUtilityNetworkLayer`, and `packageRefreshJobId`. The map area item's `properties` add
  `packageRefreshSchedule`, a Quartz cron expression (`17 56 17 ? * FRI`), beside `extent` and `status`.
- **The file is an ordinary registered replica** (§4). `GPSyncReplica` has role `esriReplicaRoleChild`, a
  `LastSyncDate` of 1970-01-01T00:00:01, and the area's envelope as its query geometry. Each `GPSyncDataset` carries
  the server generation and client generations of 1000 (download, upload, acknowledge). The file also has a
  `GDB_ReplicaLog` table, which an on-demand replica captured on 2026-11-10 did not.
- **The packaging service on Enterprise 12.1** (`Utilities/OfflinePackaging/GPServer`) has three tasks: `CreateMapArea`,
  `SetupMapArea` and `RefreshMapAreaPackage`. Creating an area by REST was not achieved: every combination of
  `areaType`, `area`, `bookmark` and `extent` tried failed. Each failure was either a JSON parse of an empty value or
  `OffPkg_ 24` *"Parameter Area is invalid or exceeded limit"*, even for a 5 km box. The documentation pages answered
  404 the same night.

**What Field Maps does with a preplanned area, measured 2026-11-12** (Field Maps 26.2.2 on the emulator, against
dev.graticula.com serving an area the way §11a of ADR-288 describes). The server's request log and Field Maps' own
log (*Profile › Troubleshooting › View logs*, verbose) were read:

- **It lists the area without being asked.** The map's area list showed "Probe area, 442 kB" under *Not downloaded*.
  To build that list it read the map's `relatedItems?relationshipType=Map2Area`, then each area's
  `relatedItems?relationshipType=Area2Package`.
- **Download fetches the package, then registers it with `createReplica`.** After `GET …/items/{package}/data`, Field
  Maps posted `createReplica` to the feature service with **`dataFormat=json`**. So a device registers its own replica
  for the package it downloaded, and this is the call that makes the copy syncable. The package's own replica is not
  synced by the device. **Measured 2026-11-12** (Field Maps 26.2.2, read from its log through our refusal, which echoes what it received), the
  whole request is:

  ```
  POST …/FeatureServer/createReplica
  async=true; f=json; transportType=esriTransportTypeUrl;
  replicaOptions={"registerExistingData":{"refReplicaId":"{75f74eb9-c9aa-4e16-9857-d605b8d3d270}"}}
  ```

  No `layers`, `dataFormat`, `geometry`, `syncModel` or `replicaName`. `refReplicaId` is the replica id of the package
  the device downloaded (braced, lower case). The *answer* ArcGIS gives was not captured. Graticula answers a new
  `perLayer` replica of the caller's (a new id, the reference's layers and generations) as a JSON replica with empty
  `features` and `attachments`, which is `INFERRED` (ADR-288 condition 3). An earlier attempt, which Graticula refused
  as `dataFormat=json` with no layers, was the same request read before its parameters were known.
- **A basemap without a package fails the whole download.** The map's basemap was a Graticula vector tile layer. Field
  Maps logged *"Layer: probe tiles - Error: Not found. Missing preplanned vector tile package"* and then *"The map
  download contains one or more errors"*. The area was marked *Download failed*. A map with a basemap therefore needs
  a tile package related by `Area2Package` for it.
- **End to end, the same morning, once the server answered all of the above.** The area (a 442 kB geodatabase and a
  6.2 MB VTPK) downloaded. Registration answered 200, and the device then asked `replicas/{its new id}` and ran a first
  `synchronizeReplica`. With the network off, a point collected on the phone reached the server at the next *Sync
  now*, and a point added on the server meanwhile appeared on the phone.
- **Two more things the device needed, found on the way:**
  - **The area's extent is stated in its map's reference.** An extent kept in degrees for a Web Mercator map made
    Field Maps refuse every location in the area (*"The location is outside the map area"*).
  - **The tile package's `fullExtent` must cover the area.** Field Maps confines locations to the basemap package's
    full extent. Graticula's package kept the service's, which is the extent of its data (a few kilometres), so a
    location inside the area but outside the data was refused. The same location inside the data was taken.

## 5c. A JSON replica *(measured 2026-11-12, one service)*

`createReplica` with `dataFormat=json`, `syncModel=perLayer`, `async=true` and `returnAttachments=true` on the Aecon
test service (ArcGIS Server 12.1, our own data). The job answered like a SQLite one (`statusUrl`, then `replicaID`,
`replicaName`, `layerServerGens`, `responseType`, `targetType`, `transportType` and `resultUrl`). The file at
`resultUrl` is one JSON object:

- `replicaName`, `replicaID`, `targetType` (`client`), `transportType` (`esriTransportTypeUrl`), `responseType`
  (`esriReplicaResponseTypeData`), `syncModel` (`perLayer`), and `layerServerGens` (`[{id, serverGen}]`).
- `layers`: one object per layer, `{id, features, attachments}`.
  - A feature is `{geometry, attributes}`, as in a query answer: Z included, dates as epoch milliseconds, the
    object id and GlobalID among the attributes.
  - An attachment is `{attachmentId, globalId, parentGlobalId, contentType, size, name, url}`. The `url` points at
    a file the server wrote into its job directory, so the bytes are fetched separately rather than embedded.

A registration-only request (`layerQueries` with `queryOption: none` and no attachments) was also sent. Its job had
not finished when it was read, and it left no replica registered. What Field Maps itself sends to register a package
is still to be read from the device.

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
