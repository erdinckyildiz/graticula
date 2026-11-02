# ADR-257 — Portal content adds are doors onto import

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the doors run Studio's own handlers and the ArcGIS API for Python 2.4.3 drove add, publish and append against them; the shapes §5.8 marks INFERRED were not measured against a real Portal |
| **Decided** | 2026-11-02, by owner decision on [Q-225](../open-questions.md): *"Evet, var olan içe aktarmanın kapısı olarak"* — yes, as a door onto the import that already exists |
| **Supersedes** | [ADR-105](ADR-105-arcgis-clients-append-through-uploads.md) §5.3 in part (`appendItemId` and the CSV and workbook formats are read now); the *not served* `createService` and `addItem` of [ADR-040](ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md)'s amended §4 note |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

The ArcGIS veteran's review of 2026-11-02 ranked four portal operations first among what stops a publisher in the
first hour, and Q-225 recorded them:

- `content/users/{user}/addItem` took a Service Definition and a Web Map only (`PortalEndpoints.ProPublish`, *is
  neither*). The ArcGIS API for Python's `Folder.add` / `gis.content.add` of a CSV, a zipped shapefile or file
  geodatabase, GeoJSON or a document was refused.
- `publish` took `filetype=serviceDefinition` only, so `item.publish()` had nothing to publish.
- `createService` had no route. ADR-040 recorded it as *not served*, with no decision.
- `FeatureServer/{id}/append` refused `appendItemId` (ADR-105 §5.3), because nothing was stored as a portal item.

Studio already does every one of these: it imports a CSV, a workbook, a zipped shapefile, a file geodatabase and
GeoJSON (ADR-038, ADR-106, ADR-112, ADR-120), keeps files as documents (ADR-188), designs an empty hosted layer
(`POST /admin/hosted/define`), makes hosted views (ADR-113) and appends a file to a hosted layer (ADR-103). The
owner's decision is that the portal reaches those, and does not grow a second importer beside them.

## 2. Alternatives considered

### Alternative A — Doors onto Studio's handlers (chosen)

Each portal operation shapes its request into the one Studio's handler reads, runs that handler inside the request,
and rewords only its answer into the portal's envelope — the pattern ADR-214 §5a set for `protect`, `reassign`,
`updateFolder` and an item's `groups`.

**Argument for.** The privilege check, the store call, the audit line and every refusal are Studio's, so the two
faces cannot drift: a limit raised, a format added or a bug fixed in Studio's import is the portal's at once. Nothing
needs its own tests beyond the shaping.

**Argument against.** Studio's handlers were written for Studio's requests. A door reads their answer back from
JSON, and swaps the request's form for the duration of the call; a change to a handler's answer shape breaks the
door silently unless a test drives it.

### Alternative B — A portal importer of its own

**Argument for.** It could follow ArcGIS's item model exactly: an item first, then any number of services published
from it, each with its own analysis of the file.

**Argument against.** It is the second implementation the owner ruled out, and the costliest kind: an importer is
where encodings, coordinate systems, archive bounds and type inference live (ADR-024, ADR-080, Q-98), and each would
have to be decided twice and kept equal.

### Alternative C — Stage the portal's file in the process, as a service definition is

**Argument for.** `addItem` for a service definition already stages the file under the import scratch directory
(ADR-191); a data file could do the same and need no document.

**Argument against.** An item the client was told exists would be gone at the next restart or scratch sweep, and
would never appear in Studio's content. The owner's instruction and Q-225 name *Studio's* content as where an added
file belongs.

## 3. Counterarguments to the preferred option

- **ArcGIS's `createService` makes an empty service**, and its clients fill it afterwards with `addToDefinition` —
  the ArcGIS API for Python's `create_service` and `create_view` both work that way. This server does not create a
  service without layers (owner decision 2026-09-06, ADR-057 condition 4), so the shape the stock client sends is
  refused, and only a one-request shape (§5.5) works. Q-225's wording — *"createService for an empty hosted layer and
  a view"* — is met for a layer with no rows, not for a service with no layers. Whether the owner meant the latter,
  and would revisit ADR-057 condition 4 for it, is not decided here.
- **`analyze` analyses nothing.** A real Portal reads the CSV, proposes field types and the coordinate columns, and
  can geocode. Here the import's own reader finds the columns when the publish runs, so a client that shows the
  analysis to a person shows little.
- **A document is not an ArcGIS data item.** It has no `Service2Data` relationship to the service published from
  it, so the ArcGIS API for Python's overwrite-from-item (`item.publish(overwrite=True)`) cannot find the service, and
  is refused (§5.3).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The ArcGIS API for Python adds a file in parts by default, then polls the item's `status` and opens the `itemId` it names | `arcgis` 2.4.3, `gis/_impl/_content_manager/folder/core.py` — `Folder.add(stream=True)` → `_add_async_streaming` → `Job.result()` → `commit` → `_process_item_status` | the installed package, read 2026-11-02 |
| `item.publish()` of a CSV calls `content/features/analyze`, merges `publishParameters`, publishes, and polls `items/{serviceItemId}/status` with `jobid` in the form | `arcgis/gis/__init__.py`, `Item._publish` and `_check_publish_status` | the installed package |
| `FeatureLayer.append` checks `supportsAppend` and `supportedAppendFormats` before it sends `appendItemId` and `appendUploadFormat` | `arcgis/features/layer.py`, `FeatureLayer.append` | the installed package |
| The documented operations and their answers | *Add Item*, *Add Part*, *Commit*, *Item Status*, *Publish Item*, *Analyze*, *Create Service*, *Append (Feature Service/Layer)* | ArcGIS REST API reference, developers.arcgis.com/rest |
| Add, publish and append work end to end from the real client | `gis.content.folders.get().add(ItemProperties(item_type=CSV), file=…).result()` → `item.publish()` → a Feature Service whose layer counts 3; `layer.append(item_id=…, upload_format="csv")` → `Completed`, count 5. `create_view` stopped in the client (*A registered hosted feature service is required*, a property this server's service document does not carry) | local fixture, 2026-11-02 |
| The doors hold Studio's rules | `PortalContentAddsTests` (conformance, 7 tests) and `PortalContentAddsAreStudiosImportTests` (unit) | `tests/Graticula.Conformance.Tests`, `tests/Graticula.Host.Tests` |

## 5. Decision

The portal's content adds are doors onto Studio's handlers, as ADR-214 §5a's are. Each runs the handler inside the
request (`PortalEndpoints.InnerAnswerAsync`), with the request's form replaced by the one the handler reads for the
length of the call; nothing is written a second way.

### 5.1 `addItem` with a file

`content/users/{user}[/{folder}]/addItem` with a `type` that is a file Studio keeps — CSV, Shapefile, File
Geodatabase, GeoJson, Microsoft Excel, and the documents of ADR-188 (PDF, Microsoft Word, Microsoft Powerpoint,
Image) — is Studio's `POST /content/documents`, by the caller, into their own content. `title` and `snippet` are the
document's; `tags` and `description` then go through Studio's tag and description handlers; a folder in the path is
the store's move. In one request the `file` is the item; with `multipart=true` the parts are staged as a service
definition's are (ADR-191), and `commit` joins them and makes the document. The answer is
`{"success":true,"id":…,"folder":…}`; the item's `status` names the document as `itemId` once committed.

Three kinds are new to the document store, and kept only when the upload names its type: a `.zip` is a Shapefile or
a File Geodatabase item only when added as one, and its listing must hold a `.shp` or a `.gdb` folder accordingly
(`HostedDataEndpoints.ArchiveRefusal`, which reads the listing and unpacks nothing); GeoJSON is plain UTF-8 text
opening with `{`, named `.geojson`, or `.json` when added as GeoJson. Studio's own upload takes the same `type` field;
its picker does not offer these kinds. No migration: the document table has no constraint on its kind.

### 5.2 `publish` of a data item

`content/users/{user}[/{folder}]/publish` with `filetype` `csv`, `shapefile`, `geojson` or `excel` hands the document's
bytes, under its file name, to Studio's `POST /admin/hosted/import`, with `name`, `x`, `y` and `srid` from
`publishParameters` (`name`, `longitudeFieldName`, `latitudeFieldName`, `sourceSR`). `fileGeodatabase` is that import's
inspection job, then Studio's `POST /admin/hosted/geodatabase` with every layer the inspection found, each awaited as
the service-definition publish awaits them. The publisher's privilege is checked first, as for a service definition;
the item must be the caller's, or they must manage all content, and be of the file type named. The service goes into
the folder of the path, with the item's description and tags. The answer is ArcGIS's `services[]` — `type`,
`serviceurl`, `encodedServiceURL`, `size`, `jobId`, `serviceItemId`, `success`. A synchronous import's `jobId` is an
opaque id the item's `status` answers `completed` for (INFERRED: ArcGIS's job ids are opaque strings); a
geodatabase's is the import job's own, which `status` follows — read from the form's `jobid` as the ArcGIS API for
Python posts it, as well as from the query.

### 5.3 What `publish` refuses

`locationType` other than `coordinates` (an address, a lookup, or `none` for a table without coordinates: the
import's reader needs coordinates or WKT and geocodes nothing); `overwrite=true` (Update data, or `append` with
`truncateExisting`, replaces a layer's rows); a file type the item is not. Each is refused by name and writes nothing.

### 5.4 `content/features/analyze`

For an item the caller may read, answers `{"publishParameters":{"type","name","locationType"?},"records":[]}`: the
service name the ArcGIS API for Python would make from the title, and `coordinates` for a table. It names no columns:
the import's reader finds them at publish, as Studio's upload does. A file or text sent to analyze is refused — add
it first.

### 5.5 `createService`

With `createParameters.layers` (and `tables`) naming each layer's `name`, `geometryType` and `fields`, each is designed
by Studio's `POST /admin/hosted/define` into one service of `createParameters.name`; every layer is read and checked
before the first is made. With `isView=true` and each layer's `adminLayerInfo.viewLayerDefinition` naming
`sourceServiceName` and `sourceLayerId`, the view is Studio's `POST /admin/services/{source}/views` with the
capabilities `createParameters` names. Without layers — the empty service ArcGIS's clients fill later — it is refused,
citing ADR-057 condition 4. The answer is ArcGIS's: `encodedServiceURL`, `itemId`, `name`, `serviceItemId`,
`serviceurl`, `size`, `success`, `type`, `isView`.

### 5.6 `append` from an item

`FeatureServer/{id}/append` with `appendItemId` reads a document the caller may read, of the format
`appendUploadFormat` names, exactly as it reads an upload (`HostedDataEndpoints.ReadUpdateFileAsync`, then
`WriteUpdateAsync`), and leaves the item where it was. `csv` and `excel` join `geojson` and `shapefile` as formats —
the reader already took them for Update data — and the layer's `supportedAppendFormats` says so.

### 5.7 Who is told what

`portal:user:createItem` follows `content:create` where publishing is served, so a member who may create content but
not publish is told they may add an item; `portal:publisher:publishFeatures` stays the publisher's. The doors exist
where `Graticula:PublishFromPro` is on, as `addItem` and `publish` always have.

### 5.8 INFERRED, for the owner to confirm

1. **The one-request `createService`** (§5.5) — layers or a view's source inside `createParameters` — is a shape no
   stock client sends. The public reference documents `createParameters` as the service's properties; reading
   `layers` there is this server's reading.
2. **ArcGIS polylines and polygons are designed multi-part** (`MultiLineString`, `MultiPolygon`), since an ArcGIS
   geometry of either kind may have several parts.
3. **`analyze` answers without analysing** (§5.4), and **`sourceSR` is the import's `srid`**.
4. **A geodatabase import still running after three minutes** answers its job, with the source item's id standing in
   for the service item that does not yet exist; a client that opens that id opens the document.
5. **The published service carries the item's description and tags**, not its title: the service's title is its name
   (ADR-214 §5 item 4).
6. **A `.json` file is GeoJSON only when added as GeoJson**; `.geojson` always is.

## 6. Consequences

**Positive.** The ArcGIS API for Python's add → publish → append path works against this server unchanged; Pro's
service-definition path is untouched. Every refusal a portal client meets is Studio's sentence.

**Negative.** A door reads a Studio handler's answer as JSON (`id`, `job`, `services.feature`); a change to those
shapes is caught by the conformance tests, not the compiler. `create_service` and `create_view` from the Python API
still fail — the first refused, the second stopped in the client — until either ADR-057 condition 4 is revisited or a
service-level `addToDefinition` exists. A document has no `Service2Data` relationship, so a publish from it cannot
be overwritten from the item.

**State.** In the catalogue, nothing new: an added file is a row of the document table Studio's upload writes (three
new values of its `kind`, which no constraint lists), and a published one is Studio's import's layer and service. At
runtime, node-local unless a site shares its server directories: a file sent in parts is staged as a service
definition is (ADR-191, ADR-012 phase 2's handoff). The job id a synchronous publish answers is not remembered at all:
it is not a GUID, so any node's item `status` finds no job to look up and answers `completed`, which it is.

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A Studio handler's answer keeps the fields a door reads: a document's `id`, an import's `services.feature`, a job's `job` | held by `PortalContentAddsTests` |

## 8. Dependencies

**Depends on**: ADR-038, ADR-057 (condition 4), ADR-103, ADR-105, ADR-106, ADR-112, ADR-113, ADR-188, ADR-191,
ADR-214 (§5a).

**Depended on by**: —

## 9. Revisit triggers

- The owner decides `createService` should make an empty service (Q-225's wording), which reopens ADR-057 condition 4
  and needs the service-level `addToDefinition` the ArcGIS clients send next.
- A client is found that reads `analyze`'s field list or `records` to decide what it publishes.
- A geocoder is served (the parallel geocoder work): `locationType=address` then has something to call.
- An overwrite from an item is asked for, which needs the item-to-service relationship recorded.

## 10. Dissent

None recorded. §3's first point is the closest: the owner's words name an empty service, and this ADR does not make
one.
