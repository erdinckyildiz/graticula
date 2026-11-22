# ADR-310 — Append on the service, into registered layers, and from addresses

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the routes are tested end to end against a running server, the geocoder against a stand-in for Photon; no real ArcGIS client has called the service-level `append` or sent `appendSourceInfo` against this server (§6) |
| **Decided** | 2026-11-21, by the coordinator's brief for ArcGIS Enterprise 12.2 band 1 (the owner approved band 1 the same day). Every point the public references do not settle is marked `INFERRED` in §5 and listed in §7 |
| **Supersedes** | — (amends [ADR-105](ADR-105-arcgis-clients-append-through-uploads.md) §5 *hosted layers only*, [ADR-257](ADR-257-portal-content-adds-are-doors-onto-import.md) §5.3 *no `locationType` but `coordinates`*) |
| **Superseded by** | — |

---

## 1. Context

ArcGIS Enterprise 12.2 changed six things in the append and administration corner that a client of this server can meet
([what's new](https://developers.arcgis.com/rest/services-reference/enterprise/whats-new/)):

1. **`FeatureServer/append`**, the operation on the *service*, answers with a `layers` array of per-layer counts.
   Only the layer's own `…/{id}/append` was mapped, so the Python API's service-level call got a 404.
2. A **reference** (registered) feature service accepts `append` in more formats — Excel, shapefile and JSON Lines. Here
   `supportsAppend` was `layer.IsHosted`, and a registered table was appended to by nobody.
3. **Points from addresses** in a CSV or a workbook, appended and published. [ADR-257](ADR-257-portal-content-adds-are-doors-onto-import.md) §9
   deferred it *"when a geocoder is served"*, and [ADR-258](ADR-258-an-optional-photon-adapter-behind-geocodeserver.md) served one.
4. **Create Replica `catalogID`** for relationships in the mobile geodatabase.
5. **`GetServiceDatastoreInfo`**, a publishing tool.
6. **Portal Announcements**.

Items 4–6 were checked and not built (§5.7–5.9).

## 2. Alternatives considered

### Alternative A — a second append for the service

**Argument for.** The service-level operation has its own parameters (`layers`, `layerMappings`); a function that reads them,
resolves the layers and writes would be short and self-contained.

**Argument against.** Everything the layer-level append does — the privilege and ownership rule ([ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md)),
the upload and item lookup, the format readers, the field mappings, the importer's transaction and what follows a write
(tiles, thumbnail, audit, webhooks) — would then exist twice, and the second copy is the one that drifts. Rejected.

### Alternative B (chosen) — the service-level operation resolves the layers and calls the layer's code

`ServiceAppendAsync` reads `layers` and `layerMappings`, resolves **every** layer through the layer-level door before any write,
reads the file once, and writes each layer with the same `WriteAsync` the layer-level `AppendAsync` calls.

### Alternative C — append into a registered layer by widening the importer

**Argument for.** One engine, binary `COPY`, no per-feature cost.

**Argument against.** `PostGisImporter` is written for the datastore's own tables: the schema is `hosted`, the geometry column is
`geom`, the key is `objectid`, and every statement is in the datastore's connection. A registered table has whatever its owner
named, may be in Oracle, MySQL, SQL Server or a DuckDB file, and is in a database this server may not even share with the
datastore. Widening it means a second importer per engine, and each would write into somebody else's table without the
checks `applyEdits` makes (domains, editor tracking, ownership, ceilings).

### Alternative D (chosen) — the file's features are the `adds` of one `applyEdits` batch

What every engine already has is the layer's writer. The file becomes an `adds` array and goes through `PrepareEditAsync` and
`LayerConnections.WriterFor`: the same refusals, the same all-or-nothing batch as `applyEdits` with `rollbackOnFailure`.

### Alternative E — geocode in a new endpoint and import the result

**Argument for.** Keeps the import path untouched.

**Argument against.** ArcGIS puts addresses in the same operations (`appendSourceInfo`, `publishParameters`), and a client sends
them there. A parallel endpoint is a feature nobody calls.

### Alternative F (chosen) — locate the rows where the file is read

`TryTableAsync` is where a CSV or a workbook becomes a dataset for Studio's import, Update data, ArcGIS `append` and the
portal's `publish`. After it reads the rows it asks the geocoder, for the one request that said `locationType: address`; the
four doors get it with no further code.

## 3. Counterarguments to the preferred options

- **A registered append is slow and bounded** (§6 and [D-465](../architecture-debt.md)): it is not `COPY`, and a file longer than
  the service's edit ceiling is refused. A nightly reload of a million rows into a registered table does not fit this and
  would be done in the database.
- **A service append across layers is not atomic** ([D-464](../architecture-debt.md)). A client that expects one transaction
  is wrong about this server.
- **Unmatched rows are dropped**, where a user might expect them kept without a location. A layer with null geometries is a
  worse layer to draw, and a count that silently excluded rows is worse than one that says which.
- **Addresses leave the server.** To the Photon the operator named, as ADR-258 §5.1 says and nowhere else; an operator who does
  not want that does not configure one.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The service-level operation's parameters and its 12.2 answer | `layers`, `layerMappings` (`id`, `sourceTableName`, `fieldMappings`), `appendUploadId`, `appendItemId`, `appendUploadFormat`, `upsert`; the job answer with `recordCount`, `layers` (`recordCount`, `attachmentCount`, `id`, `layerName`), `operationName`, `lastUpdatedTime`, `layerName`, `submissionTime`, `status` | [Append (Feature Service)](https://developers.arcgis.com/rest/services-reference/enterprise/append-feature-service/), read 2026-11-21 |
| The service document says `supportsAppend` and `supportedAppendFormats`, and 12.2 adds `geojson` to the service-level list | the syntax section and the 11.5 and 12.2 notes | [Feature Service](https://developers.arcgis.com/rest/services-reference/enterprise/feature-service/) |
| A reference service takes Excel, shapefile and JSON Lines at 12.2; JSON Lines is *Javascript Object Notation Lines* in the supported formats | the 12.2 section and the format list | [Append (Feature Service/Layer)](https://developers.arcgis.com/rest/services-reference/enterprise/append-feature-service-layer/) |
| `appendSourceInfo` is the publish parameters `analyze` returns and carries `locationType`, the coordinate field names and `sourceSR`; geocoding is supported for CSV and Excel | the parameter and its example | the same page |
| A CSV's address is `locationType: address`, `addressTemplate` (`"{Address} {City}, {State} {Zip}"`) or `addressFields` (a role → column object), `sourceCountry` (two letters) | the CSV and Excel `publishParameters` | [Publish Item](https://developers.arcgis.com/rest/users-groups-and-items/publish-item/) |
| What ArcGIS does with an unmatched row is not documented | the publish page names `persistErrorRecordsForReview` in an example and describes nothing | the same |
| The service append, a registered append, JSON Lines, a bad line named, refusals of `truncateExisting` and `upsert` on a registered layer, a geometry type that is not the layer's | `AppendServiceAndRegisteredTests` (4) against a running server | `tests/Graticula.Conformance.Tests` |
| Addresses: refused naming `Graticula:Geocoding:PhotonUrl` with no geocoder; with one (a stand-in for Photon on the loopback), two of three rows located, row 2 reported, a template naming no column refused before the engine is asked; and a publish by address | `AppendServiceAndRegisteredTests.A_csv_of_addresses…`, `PortalContentAddsTests.A_csv_of_addresses…` run on both fixtures | the same |
| The geocoding rules in isolation: template and role order, an empty part, no geocoder, a caller the sharing does not admit, more rows than `MaxBatchSize`, an engine that is down, nothing matched | `AddressGeocodingTests` (10 cases) | `tests/Graticula.Host.Tests` |
| No code, document or tool in this repository names `GetServiceDatastoreInfo`, `catalogID` or a portal announcement | `grep -ri` over `src`, `docs`, `tools`, 2026-11-21 | — |
| The mobile geodatabase this server writes has no relationship classes, only the attachment relationship | `IMobileGeodatabaseWriter`, `OfflineReplicaShapes` (the one *relationship* is an attachment's); no `GDB_Items`-style relationship table is written | `src/Graticula.Api.ArcGis/Offline`, `src/Graticula.Host/OfflineReplicaShapes.cs` |

## 5. Decision

### 5.1 The service-level append

`POST /rest/services/{folder}/{service}/FeatureServer/append`, governed like the layer's (`ByPrivilege`).
`layers` (a JSON array of ids) and `layerMappings` (`id`, `sourceTableName`, `fieldMappings`) name the layers; with neither, a
service of one layer is that layer; two or more layers need `layerMappings` (§5.4). Each layer is resolved by the layer-level
door — `content:publishFeatures` and ownership for a hosted layer, `features:edit` for a registered one — **before the first
write**, so a refused layer refuses the request with nothing written. The file (`appendUploadId` or `appendItemId`,
`appendUploadFormat`) is read once and written into each layer by the code the layer-level append uses.

The answer is the 12.2 job's: `recordCount` (the sum), `layers` (`id`, `layerName`, `recordCount`, `attachmentCount` 0),
`operationName` `append`, `lastUpdatedTime`, `layerName` (the last layer's), `submissionTime`, `status` `Completed`. **It is
the finished job rather than `processing` and a status address**, as the layer-level append has answered since
[ADR-105](ADR-105-arcgis-clients-append-through-uploads.md) (`async=true` refused). `INFERRED`: `Completed` rather than the 12.2 example's `COMPLETED`, to
match the layer-level answer a real client has been run against.

`supportsAppend` and `supportedAppendFormats` appear on a **service** document when one of its layers is hosted
(`AppendFlags`), and not before this route worked.

### 5.2 Registered layers

A layer is **registered for this purpose** when the importer may not write it: its source is not the datastore, or the datastore
serves it from a schema other than `hosted` ([ADR-058](ADR-058-the-datastore-schema-is-edited-from-the-screen.md) §5h). Its
`append` — layer-level or service-level — turns the file's features into the `adds` of one batch and applies them through
`PrepareEditAsync` and the layer's writer, exactly as `applyEdits`. Asked first: `features:edit`, adds only (ADR-075).

[ADR-103](ADR-103-a-hosted-layer-is-updated-from-a-file.md) §5.1 said *hosted layers only*, and [ADR-058](ADR-058-the-datastore-schema-is-edited-from-the-screen.md)
§5h refuses a registered table's **schema** to this server. Neither forbids adding rows to one: [ADR-205](ADR-205-a-registered-oracle-database-is-served-like-postgis.md),
[ADR-207](ADR-207-attachments-and-history-on-registered-layers.md), [ADR-208](ADR-208-a-registered-mysql-database-is-served-like-postgis.md),
[ADR-210](ADR-210-a-registered-sql-server-database-is-served-to-read.md) and [ADR-211](ADR-211-attached-duckdb-and-motherduck-tables-are-edited.md) edit them, and the
v1 scope says *read/write where rights allow*. What stays refused on a registered layer, each in a sentence and with nothing
written: `truncateExisting` (emptying a table in the database it was registered from is that database's owner's), `upsert`,
and a source this server only reads (`Writable` false). The native `POST /admin/hosted/{layer}/append` is **unchanged** and still
refuses a registered layer ([ADR-103](ADR-103-a-hosted-layer-is-updated-from-a-file.md) condition 2's test holds).

A registered layer's document says `supportsAppend` when its caller may add features (`Create` in the capabilities the document
already computes per caller) and the source takes writes.

**`supportedAppendFormats` is `geojson,jsonl,shapefile,csv,excel`**, for a hosted layer, a registered one and a service: one
reader, one list. 12.2 names JSON Lines for reference services; offering it on hosted ones too costs nothing (the reader is the
same) and keeps one list rather than two.

### 5.3 JSON Lines

One GeoJSON `Feature` on each line, blank lines ignored, read into a feature collection and by the GeoJSON reader, so its
limits and its CRS rule (4326) apply. A line that is not a JSON object is refused with its number; an upload is the only way in
(an item has no JSON Lines type here). `INFERRED` ([D-467](../architecture-debt.md)): the reference does not say what a line holds.

### 5.4 `layerMappings` over a file that holds one table

A file this server reads holds **one table** (a GeoJSON collection, a shapefile, a sheet). So `sourceTableName` is accepted and
not checked against anything, `INFERRED`; two or more target layers need `layerMappings`, which says that the same table going
into each is meant — otherwise the request is refused with nothing written, because a file appended twice by a client that
meant two files is a duplicate nobody finds. Only one source can map to a layer: a layer named twice in `layerMappings` is
refused, as the reference says. `layerMappings` naming a layer `layers` does not is refused.

### 5.5 Addresses

`locationType: address` for a CSV or a workbook, in four doors: ArcGIS `append` (as `appendSourceInfo`), Studio's Update data and
import (as form fields), and the portal's `publish` (as `publishParameters`). The columns are named by `addressTemplate`
(`{Column}` slots, any case) or `addressFields` (`Address`, `Neighborhood`, `City`, `Subregion`, `Region`, `Postal`,
`CountryCode` → a column; joined in that order, [ADR-258](ADR-258-an-optional-photon-adapter-behind-geocodeserver.md) §5.5), and `sourceCountry` limits the search to
countries as `findAddressCandidates` does. `appendSourceInfo` also now carries the coordinate columns
(`longitudeFieldName`, `latitudeFieldName`) and `sourceSR` into the reader, which ignored it.

**ADR-258's rules, applied.**

- *Optional.* With no geocoder (`Graticula:Geocoding:PhotonUrl` unset) the request is **refused, in a sentence that names the key**,
  and nothing is written. `lookup` and `none` stay refused by name.
- *Rate.* The rows go through the same bounded batch as `geocodeAddresses` (`GeocodeServerEndpoints.BestAsync`, extracted from it
  and shared): four at a time, the configured deadline, an engine that is down failing the batch with 503. More rows than
  `MaxBatchSize` (200 by default) are **refused whole**, not cut.
- *Privacy.* The addresses go to the operator's Photon and nowhere else; nothing of them is logged or audited. Only a caller the
  geocoder's own sharing admits (`GeocodeServerEndpoints.Allowed` — `organization` by default) may have rows geocoded, else 403:
  the append must not be a way around the locator's own sharing.
- *Air gap.* An air-gapped deployment runs Photon inside (ADR-258 §5.1); nothing here reaches further.

**An unmatched row is not written, and is reported** (`INFERRED`, [D-466](../architecture-debt.md)): the public references say
nothing. The ArcGIS `append` answer and the native import and update answers carry
`geocoding {rows, matchedCount, unmatchedCount, unmatchedRows}` (the first hundred row numbers); a table nothing matched is
refused. `publish` has no place for it (D-466).

### 5.6 Not built, and why

- **Create Replica `catalogID` for relationships (§1.4): skipped.** `catalogID` is a field of the mobile geodatabase's catalogue of
  relationship classes. The replica this server writes ([ADR-288](ADR-288-offline-sync-is-in.md)) has no relationship class
  at all — the one *relationship* in it is a feature's attachments — so there is no field of ours to name. It becomes
  buildable when a replica carries relationships, which is its own decision.
- **`GetServiceDatastoreInfo` (§1.5): skipped.** It is a publishing geoprocessing tool. Nothing in this repository names it; the
  requests Pro makes when it publishes to this server are recorded ([ADR-191](ADR-191-arcgis-pro-publishes-hosted-feature-layers.md),
  [ADR-299](ADR-299-pro-publishes-by-reference-to-a-registered-database.md)) and it is not among them. Answering a call nobody
  makes is a feature nobody calls.
- **Portal Announcements (§1.6): skipped.** `SiteAnnouncements` is a node-to-node channel, as the brief says, and no client —
  Map Viewer, Studio or Pro — reads an organisation-level announcement from this server, so there is nothing to map onto.
  Built the day one does.

## 6. Consequences

**Positive.** The Python API's service-level append finds the route and the 12.2 answer; a registered layer takes an upload, with
every rule `applyEdits` has; a CSV of addresses becomes a layer where the operator has a geocoder, and says plainly where it
cannot; one reader and one list of formats.

**Negative.** Three debts, each with its trigger: a service append is not atomic across layers ([D-464](../architecture-debt.md)), a
registered append is an edit batch with its ceiling and no upsert or truncate ([D-465](../architecture-debt.md)), a publish by
address does not report the rows it dropped ([D-466](../architecture-debt.md)); and JSON Lines is read as GeoJSON features, a guess
([D-467](../architecture-debt.md)). The real ArcGIS client has not been run against the service-level route or `appendSourceInfo`
(confidence `MEDIUM`). `ArcGisAppendEndpoints` grew by two files and a shared `WriteAsync`.

**State.** None in the catalogue and no migration. At runtime an upload waits where [ADR-105](ADR-105-arcgis-clients-append-through-uploads.md) keeps it (an hour, this node's scratch, or the site's shared directory under ADR-012 phase 2) and is spent by the append that uses it; the geocoder is the one Photon configuration of ADR-258 and every node of a site answers alike when they share it. Nothing of an address is kept.

**Ports created.** None. No Tier 2 dependency is adopted; the geocoder is behind `IGeocoder` (ADR-258) and the writer behind
`IFeatureWriter`.

## 7. Assumptions this decision rests on

`INFERRED`, listed for the owner to confirm: **Confirmed by the owner 2026-11-22, all as written** (*"Hepsi yazıldığı gibi"*), together with the release as v1.0.528.

1. The service-level answer is the finished job with status `Completed`, not `processing` and a status address (§5.1).
2. `sourceTableName` is not checked, and several layers need `layerMappings` (§5.4).
3. Registered means *the importer may not write it*, which includes a datastore layer in a foreign schema (§5.2).
4. A registered append refuses `truncateExisting` and `upsert` (§5.2).
5. `jsonl` is offered on hosted layers too, and a line is a GeoJSON feature (§5.2, §5.3).
6. An unmatched row is dropped and reported, and a table nothing matched is refused (§5.5).
7. More rows than `MaxBatchSize` are refused, not cut (§5.5).
8. A caller outside the geocoder's sharing may not have rows geocoded (§5.5).
9. Items 4, 5 and 6 are skipped for want of a field, a caller or a client (§5.6).

| ID | Assumption | Status |
|---|---|---|
| — | None of the register's assumptions is touched | — |

## 8. Dependencies

**Depends on:** [ADR-103](ADR-103-a-hosted-layer-is-updated-from-a-file.md), [ADR-105](ADR-105-arcgis-clients-append-through-uploads.md),
[ADR-116](ADR-116-an-update-may-upsert.md) (upsert on a hosted layer), [ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md),
[ADR-257](ADR-257-portal-content-adds-are-doors-onto-import.md), [ADR-258](ADR-258-an-optional-photon-adapter-behind-geocodeserver.md).

**Depended on by:** none yet.

## 9. Revisit triggers

- The real ArcGIS API for Python is run against the service-level `append`, `appendSourceInfo` or a JSON Lines upload and
  expects another answer, status or line shape.
- A client or the owner wants a service append to be one transaction ([D-464](../architecture-debt.md)).
- A registered layer is appended to with more rows than its ceiling, or asked to upsert ([D-465](../architecture-debt.md)).
- A client creates a replica with relationships, sends `GetServiceDatastoreInfo`, or reads an organisation announcement: items 4,
  5 and 6 are then built.
- Esri documents what a publish does with an unmatched address.

## 10. Dissent

None recorded. The skipped items 4–6 are the coordinator's brief's own condition (*implement only if a client calls it*), not
a disagreement.
