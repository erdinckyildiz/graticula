# ADR-257 — Portal content adds are doors onto import

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the doors run Studio's own handlers and the ArcGIS API for Python 2.4.3 drove add, publish and append against them, and `create_service` and `create_view` since §11; the shapes §5.8 marks INFERRED were not measured against a real Portal |
| **Decided** | 2026-11-02, by owner decision on [Q-225](../open-questions.md): *"Evet, var olan içe aktarmanın kapısı olarak"* — yes, as a door onto the import that already exists |
| **Supersedes** | [ADR-105](ADR-105-arcgis-clients-append-through-uploads.md) §5.3 in part (`appendItemId` and the CSV and workbook formats are read now); the *not served* `createService` and `addItem` of [ADR-040](ADR-040-the-portal-surface-is-how-arcgis-pro-connects.md)'s amended §4 note |
| **Superseded by** | — |

> **Amended 2026-11-02 by owner decision — §11.** Asked whether the ArcGIS API for Python's `create_service` and
> `create_view` should work as they are, the owner answered *"Boş servise izin ver"* — allow the empty service. The
> portal's `createService` without layers makes an empty hosted service, and the service's `addToDefinition` adds its
> layers or makes it a view. This reopens [ADR-057](ADR-057-composing-and-publishing-a-service.md) §5h and condition 4
> for the portal only. §3's first point, §5.5's last sentence and §9's first trigger are answered there.

> **Amended 2026-11-03 — §12.** The ArcGIS veteran's review of v1.0.499 found `manager.overwrite(csv)` failing at its
> first step. A service the portal's publish makes is now related to its data item as ArcGIS's `Service2Data`
> (migration 126), `relatedItems` answers it both ways, and `publish` with `overwrite=true` is Studio's Overwrite of
> that service's layer. That the finding is to be fixed is the owner's instruction for the review; **how it is fixed is
> INFERRED** and listed in §12.5. §5.3's refusal of `overwrite=true` is struck there.

> **Amended 2026-11-03 — §13** (the ArcGIS veteran's review, no owner decision): the hosted service's
> `updateDefinition` accepts the derived keys where they agree, wires `editorTrackingInfo` to Studio's editor-tracking
> work, and refuses every other key by name. Three choices in it are `INFERRED`.

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
  and would revisit ADR-057 condition 4 for it, is not decided here. ***(Answered 2026-11-02 — §11: the owner allowed
  the empty service, and both calls work.)***
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
| `create_service` and `create_view` work unchanged once the empty service is allowed (§11) | `gis.content.create_service(name, service_type="featureService", create_params=…)` → an empty Feature Service, 0 layers 0 tables; `FeatureLayerCollection.fromitem(item).manager.add_to_definition({"layers":[…]})` → `{"success":true,"layers":[{"id":0,…}]}`; `edit_features` of two points → both succeeded, query count 2; `flc.manager.create_view(name=…)` → a Feature Service with one layer, `isView` true, query count 2. `tools/sdk-probe/python-create-service.py` | local fixture, ArcGIS API for Python 2.4.3, 2026-11-02 |

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
import's reader needs coordinates or WKT and geocodes nothing); ~~`overwrite=true` (Update data, or `append` with
`truncateExisting`, replaces a layer's rows)~~ *(answered since 2026-11-03 — §12: an overwrite of the service the item
was published to)*; a file type the item is not. Each is refused by name and writes nothing.

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
capabilities `createParameters` names. ~~Without layers — the empty service ArcGIS's clients fill later — it is refused,
citing ADR-057 condition 4.~~ Without layers it makes an empty hosted service, since 2026-11-02 (§11). The answer is ArcGIS's: `encodedServiceURL`, `itemId`, `name`, `serviceItemId`,
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
7. **An empty service asked for as a view is an ordinary empty service until its `addToDefinition`** names the source
   (§11.2); the source is not in the `createService` request.
8. **A service's `addToDefinition` answers the layers it added** beside `success` (§11.3).
9. **The admin layer document is minimal** — `id`, `name`, `type`, `geometryType`, the view's definition (§11.4).
10. **Repeated slashes in a request path are one**, as the portals the ArcGIS API for Python works against must treat
    them (§11.4).
11. **Studio itself does not create an empty service** (§11.1): the owner's words allow the empty service for the
    Python API's two calls, and this reads them as not putting the empty step back on the Publish screen.

## 6. Consequences

**Positive.** The ArcGIS API for Python's add → publish → append path works against this server unchanged; Pro's
service-definition path is untouched. Every refusal a portal client meets is Studio's sentence.

**Negative.** A door reads a Studio handler's answer as JSON (`id`, `job`, `services.feature`); a change to those
shapes is caught by the conformance tests, not the compiler. `create_service` and `create_view` from the Python API
still fail — the first refused, the second stopped in the client — until either ADR-057 condition 4 is revisited or a
service-level `addToDefinition` exists. ***(Both done 2026-11-02, §11; both calls work from the client unchanged.)*** A document has no `Service2Data` relationship, so a publish from it cannot
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

- ~~The owner decides `createService` should make an empty service (Q-225's wording), which reopens ADR-057 condition 4
  and needs the service-level `addToDefinition` the ArcGIS clients send next.~~ **Met 2026-11-02 and acted on — §11.**
- A client is found that reads `analyze`'s field list or `records` to decide what it publishes.
- A geocoder is served (the parallel geocoder work): `locationType=address` then has something to call.
- An overwrite from an item is asked for, which needs the item-to-service relationship recorded.

## 10. Dissent

None recorded. §3's first point is the closest: the owner's words name an empty service, and this ADR does not make
one.
***(Superseded 2026-11-02 by §11: the owner chose the empty service.)***

## 11. Amendment, 2026-11-02 — the empty service, and a service's `addToDefinition`

**Owner decision, stated directly.** Asked whether the ArcGIS API for Python's `gis.content.create_service(...)` and
`FeatureLayerCollection.manager.create_view(...)` should work as they are — both make an empty hosted feature service
and then add layers with `addToDefinition`, which ADR-057 condition 4 forbade — the owner answered: *"Boş servise izin
ver"* (allow the empty service). Recorded here rather than as a new ADR because it completes this ADR's own first
revisit trigger with this ADR's own doors, and nothing in it is a decision this ADR did not already frame.

### 11.1 What an empty service is

- **Made only by the portal's `createService`** with no `layers` or `tables` in `createParameters` — Studio and Server
  still compose a service with its layers. `POST /admin/featureservices` stays retired and `POST /admin/publish` still
  refuses an empty composition (ADR-057 §5h as amended). The empty container is the ArcGIS client protocol's, reached
  through that protocol's door. `IAdminCatalog.CreateEmptyServiceAsync` writes the service and its item in one
  statement (ADR-056 §6), private, in `hosted`, owned by the caller; the path's folder moves it there, as a filled one is.
- **Its name** is a view's rule (letters, digits, `_`, `-`, at most 40), which the client's own check falls inside, and
  is refused when a service or a layer already has it.
- **What `createParameters` says that this server keeps is kept**: `serviceDescription` (the item's description),
  `copyrightText` (its credits), and `capabilities` as the editing offered — `Editing` meaning Create, Update and
  Delete. The rest (`maxRecordCount`, `initialExtent`, `hasStaticData`, `xssPreventionInfo`, `spatialReference`, …) is
  passed over: the page size is the administrator's (ADR-084), and a layer's reference is its own.
- **It answers as ArcGIS's empty service does**: the FeatureServer root with `layers: []` and `tables: []`, its
  `serviceItemId`, and an extent in 4326 until it has a layer (`FeatureServerMetadataWriter` already wrote that state).
  The admin document `GET /rest/admin/services/hosted/{name}/FeatureServer` carries `serviceItemId` too, because
  `create_view` reads it from the manager's properties and stops without it — the stop §4 recorded.
- **Studio shows it honestly.** It was already a state Studio drew, because unpublishing a last layer leaves one: Server's
  services list carries it with its Delete; Studio's service page draws the Layers card with one row — *This service
  holds no layers yet. An ArcGIS client that created it adds them with addToDefinition; one left behind when its last
  layer was unpublished can be deleted from Server's Services list.* The Empty services sweep says both ways one comes
  about, and still lists before it removes, which is what keeps a service waiting for its layers from being taken for
  residue. **Studio does not create one itself**: there is nothing a person composing on the Publish screen gains from
  an empty step, which was §5h's reason and is unchanged.

### 11.2 `createService` with `isView=true` and no layers

An empty service too. A view's source is not in the request — the ArcGIS API for Python names it only in the
`addToDefinition` that follows — so the service is bound to its source by that call. What the request does decide is
kept now: `capabilities` becomes the view's ceiling and its edits offered, as Studio's view sets them (ADR-113 §5.3).
The answer's `isView` echoes the request. INFERRED: until its `addToDefinition` arrives the service is an ordinary
empty one, and a designed layer added to it first would make it an ordinary service.

### 11.3 `POST /rest/admin/services[/{folder}]/{name}/FeatureServer/addToDefinition`

ArcGIS's hosted-service admin operation, at both spellings (`/FeatureServer` and `.FeatureServer`), beside the
`updateDefinition` already served there. The caller must publish and manage the service. `async=true` is refused (no
status URL is kept); keys other than `layers` and `tables` are refused by name — a service's own properties are
`updateDefinition`'s.

- **Layers with a geometry type and fields** are Studio's `POST /admin/hosted/define`, one per layer, into this service
  — `PortalEndpoints.DefineLayersAsync`, the same reader the one-request `createService` uses
  (`TryReadLayerDesign`). Every layer is read and its name checked before the first is made. A layer's reference is its
  `spatialReference` or its `extent`'s, 102100 read as 3857, Web Mercator when it names none; `hasZ`/`hasM` design a
  Z or M column. Keys that describe what this server works out itself (`objectIdField`, `displayField`, scales,
  `hasAttachments: false`, …) are passed over; anything else — `drawingInfo`, `templates`, `types`, `relationships`,
  `hasAttachments: true` — is refused by name with nothing made, since a client told `success` about a definition half
  applied builds on the half that is not there (ADR-008 §2). The service must be hosted and not a view. A layer's name is
  unique across the site here, as every hosted layer's is, so a name another service's layer has is refused (409).
- **Layers whose `adminLayerInfo.viewLayerDefinition` names `sourceServiceName` and `sourceLayerId`** are Studio's view
  (`AdminEndpoints.AddViewLayersAsync`, which shares `PlaceViewLayersAsync` with `POST /admin/services/{name}/views`):
  the caller must manage the source; the target must be empty or already a view of that source, and the first call
  makes it one (`MakeViewAsync`). A layer-level `viewDefinitionQuery` is the view's filter. `sourceLayerFields` other
  than `*` and `viewLayerDefinition.filter` (an area) are refused by name: Studio's view shows every field and filters
  by rows. A view layer is named for the view, not by the `name` the client sends (ADR-113's rule, so the admin pages
  that find a layer by its name stay unambiguous). A mix of view and designed layers is refused.
- **The answer** is `{"success": true, "layers": [{"id", "name"}]}` (INFERRED: the public reference shows `success`;
  the layer list is what ArcGIS Online's hosted services add, and costs a client nothing).

### 11.4 Two things the client needed that were not about the empty service

- **`GET /rest/admin/services/…/FeatureServer/{id}`** answered 404. `create_view` names each view layer from
  `layer.manager.properties`' `id` and `name`, so it now answers the layer's `id`, `name`, `type`, `geometryType`,
  its `viewDefinitionQuery` and, for a view, `adminLayerInfo.viewLayerDefinition` — a minimal admin layer document, not
  ArcGIS's full one (INFERRED: the full one repeats the REST layer document, which a client reads at its own address).
- **Repeated slashes in a path are one.** `create_view` posts to `resturl + "/content/users/…/createService"` with a
  resturl that already ends in `/`, so the address arrives as `/sharing/rest//content/…`, which this server answered
  404. The path is collapsed before anything reads it (`Program.cs`, the address middleware ahead of routing).
  INFERRED: every portal the client works against answers that address, since the client has sent it for as long as
  `create_view` has existed.

### 11.5 Evidence and state

The ArcGIS API for Python 2.4.3 against a local fixture: §4's last row. Conformance:
`PortalContentAddsTests.An_empty_service_is_created_and_filled_by_its_addToDefinition` and
`An_empty_view_becomes_a_view_of_the_source_its_addToDefinition_names` (the empty create, its documents and its listing
in Studio, a refusal by name that makes nothing, a member without the privilege refused, the layer added and edited,
the view bound and counted, the two-slash address); unit: `PortalContentAddsAreStudiosImportTests`' layer-definition
cases; console: `EmptyServicePageTests` (the empty-state sentence visible on Studio's service page, the row's Delete
visible in Server's list).

**State.** In the catalogue only — a service row and its item, written in one statement — and nothing new at runtime.

### 11.6 Left out

- `addToDefinition` of service-level properties, of a layer's renderer, templates, types, relationships, indexes or
  attachments — each refused by name.
- A view of a subset of fields or of an area, and a view of more than one source.
- `async=true` and its status URL.
- A view's `createParameters` other than `capabilities` (`isUpdatableView`, `sourceSchemaChangesAllowed`) — passed over;
  the capabilities decide what a view offers here.

## 12. Amendment, 2026-11-03 — `Service2Data`, and overwrite from the data item

### 12.1 What failed

The ArcGIS veteran's review of v1.0.499: the ArcGIS API for Python's `FeatureLayerCollection.manager.overwrite(csv)`
(`arcgis/features/managers.py`, 2.4.3) reads the service's `serviceItemId`, asks that item for
`related_items("Service2Data", "forward")`, and stops with *Cannot find related data item used to publish this Feature
Layer* when there is none. `relatedItems` was a stub answering `{"total":0}` for every item, and the portal's publish
refused `overwrite=true` (§5.3). Past that first step the client checks the file's name against the data item's
`name`, reads `info/publishParameters.json` of the service item, replaces the data item's file with
`items/{id}/update`, and calls the data item's `publish(overwrite=True)` — which, for a CSV, Shapefile, File
Geodatabase or Excel item, asks the data item for `related_items("Service2Data", "reverse")`, analyses it again and
posts `publish` with `overwrite=true`.

### 12.2 What ArcGIS documents

*Related Items*: `relationshipType` (one or several, comma-separated) and `direction` (`forward` from the origin,
`reverse` from the destination), answering `total` and `relatedItems`; `Service2Data` relates a service (origin) to
the data item it was published from (destination). *Publish Item*: `overwrite=true` replaces the service the item was
published to, keeping the service's item id and URL.

### 12.3 What is built

1. **Migration 126, `item_relationship`** — origin item, destination item, type (`Service2Data` only, by a check), the
   time, and the organisation, floored as every organisation table is (ADR-204). Both ends reference `item` and go with
   it; the recycle bin, which follows the store's foreign keys, keeps the relationship with whichever end it holds and
   puts it back on restore (measured: a service deleted into the bin and restored is related to its CSV again).
   `ComponentSchemaVersion` is 126.
2. **The portal's publish relates what it made** (§5.2): the new service to the data item, `Service2Data`.
3. **`content/items/{id}/relatedItems`** answers from the table, in either direction, each related item as the portal
   writes it and only those the caller may see. A service is found by its own item id or any of its faces'. Types this
   portal does not keep answer none; a `direction` other than `forward` or `reverse` is refused.
4. **`content/items/{id}/info/publishParameters.json`** for a service: its `name`, and for one with a `Service2Data`
   item, `type` (the file type) and `locationType: coordinates`.
5. **`publish` with `overwrite=true`** finds the one service the item was published to and runs Studio's own Overwrite
   (`HostedDataEndpoints.OverwriteLayerAsync`, the handler of `/admin/hosted/{layer}/overwrite`, now able to name the
   service so a layer name two services share is not ambiguous) on that service's layer, with the item's file as it now
   is and `x`, `y`, `srid` from `publishParameters` where named. Studio's privilege, its protection rule (no overwrite of
   a protected item), its reader and its `layer.overwrite` audit line hold. The answer is §5.2's `services[]`, with the
   existing service's item id.

### 12.4 Evidence

The ArcGIS API for Python 2.4.3 against the local fixture (`tools/sdk-probe/python-portal-doors.py`): a three-row CSV
added with `Folder.add` and published; `related_items` forward from the service gives the CSV and reverse from the CSV
gives the Feature Service; the CSV rewritten with five rows under the same name; `manager.overwrite(path)` →
`{'success': True}`; the layer then counts 5 under the same item id. Conformance:
`PortalContentAddsTests.A_data_items_service_is_related_to_it_and_overwritten_from_its_changed_file` and
`An_overwrite_of_an_item_never_published_is_refused`.

### 12.5 INFERRED, for the owner to confirm

1. **One service, one layer.** An item related to no service or to several, and a service of more than one layer, are
   refused by name: one file replaces one layer, as Studio's Overwrite does. ArcGIS overwrites a multi-layer service
   from a geodatabase; this does not.
2. **`publishParameters.json` is reconstructed, not kept**: this portal does not store the parameters a service was
   published with.
3. **Only the portal's own publish records the relationship.** A service made in Studio from an uploaded file, or
   published before migration 126, has no `Service2Data` item, and the Python API's overwrite of it stops at its first
   step, as before; Studio's Overwrite remains the way.
4. **The three flags the Python API puts on the data item's `update` before it publishes again** — `overwrite`,
   `overwriteService` and `useDescription` (`_gen_overwrite_publishParameters`, on Enterprise) — are passed over as
   echoes (ADR-214 §5 item 1's list). They were refused by name, and the client went on regardless, ignoring the
   refusal; they are instructions to the publish that follows rather than fields of the item.

### 12.6 Left out

- `Map2Service`, `WMA2Code` and ArcGIS's other relationship types, and `addRelationship` / `deleteRelationship`: nothing
  here makes them, and a portal client asking for them is answered none.
- Overwrite of a service published from a file geodatabase or a shapefile of several layers.
- Keeping `publishParameters` as sent.

## 13. Amendment, 2026-11-03 — `updateDefinition`'s keys

**Found by the ArcGIS veteran's review of v1.0.499.** `POST /rest/admin/services[/{folder}]/{name}/FeatureServer/updateDefinition`
changed four keys — `capabilities`, `serviceDescription` (or `description`), `copyrightText`, `maxRecordCount` — and
refused the whole request for any fifth, so the script every ArcGIS administrator has, `{"capabilities":"Create,Update,
Delete,Query,Editing","hasStaticData":false}`, was refused for `hasStaticData`.

**What ArcGIS documents.** *Update Definition (Feature Service)* (developers.arcgis.com, read 2026-11-03) shows, besides
the four: `hasStaticData`, `allowGeometryUpdates` (sent as the string `"true"` in one of its own examples),
`editorTrackingInfo` (`enableEditorTracking`, `enableOwnershipAccessControl`, `allowOthersToUpdate`,
`allowOthersToDelete`), `syncEnabled`, `name`, `units`, `xssPreventionInfo`, `initialExtent`, `preferredTimeReference`,
`ownershipBasedAccessControlForFeatures` and `layerOverridesEnabled`. The ArcGIS API for Python's
`manager.update_definition` posts whatever dictionary it is given.

**What is built** (`ArcGisAdminEndpoints.Service.cs`). The whole definition is still checked before anything is written.

- **Kept, as before:** the four keys, through the writers Studio's own screens use.
- **Derived keys, accepted when they agree and refused with the reason when they contradict:** `hasStaticData` (true
  exactly where no edit is offered once the definition is applied), `allowGeometryUpdates` (true exactly where Update is
  offered — an update here may move a feature and change its attributes alike, so there is no attribute-only update to
  map `false` to), `syncEnabled` (false; ADR-082), `name` (as it is; a service is renamed from its item). Agreeing is not
  an error; contradicting would be a setting this server cannot hold.
- **`editorTrackingInfo`, wired to Studio's handlers** (ADR-064, ADR-115, ADR-212): `enableEditorTracking` turns
  Portal's four columns on or off on every layer of the service, through the same work `POST`/`DELETE
  /admin/hosted/{layer}/editor-tracking` does (now `HostedDataEndpoints.TrackEditsOfAsync` and `StopTrackingOfAsync`);
  `enableOwnershipAccessControl` is ADR-115's own-features rule. `allowOthersToUpdate`/`allowOthersToDelete` must say
  what the rule implies — false where it is on, true where it is off — because ADR-115 holds editors for update and
  delete together; ArcGIS's own example (`allowOthersToUpdate: true` with ownership on) is a split this server does not
  keep and is refused by name. `allowOthersToQuery` and `allowAnonymousToQuery` false, and `allowAnonymousToUpdate`/
  `allowAnonymousToDelete` true, are refused (whoever may read queries; a signed-out caller never edits, ADR-075).
  `ownershipBasedAccessControlForFeatures`, the layer-level form, is read the same way. A view's tracking is its
  source's: turning it on or off on a view is refused.
- **Documented keys not kept, each refused by name with why:** `syncCapabilities`, `xssPreventionInfo`, `initialExtent`,
  `fullExtent`, `units`, `spatialReference`, `preferredTimeReference`, `layerOverridesEnabled`.
- **Any other key** is refused by name, with the sentence listing what is accepted. Nothing is silently ignored.

**What is not all-or-nothing.** Turning tracking on alters each layer's table in its own statement (ADR-058 §5g's
bounded ALTER), so a refusal on the second layer leaves the first changed; the refusal says which layers were changed
and that the definition's other keys were applied, as Studio's button says it for one layer.

**INFERRED, for the owner to confirm:**

1. A derived key that agrees is accepted rather than refused as *not changed here* — the owner's instruction named
   `hasStaticData`; `allowGeometryUpdates`, `syncEnabled` and `name` were given the same treatment.
2. `editorTrackingInfo` applies to every layer and table of the service, as ArcGIS's service-level key does; a layer
   with tracking already given to other columns on its Fields page is left as it is rather than given Portal's four.
3. The split *others may update but not delete* is refused rather than approximated by the stricter rule.

**Evidence.** The ArcGIS API for Python 2.4.3 against the local fixture, 2026-11-03: the typical script and
`allowGeometryUpdates: "true"` answered `success`; `hasStaticData: true`, `allowGeometryUpdates: false`,
`syncEnabled: true`, `xssPreventionInfo` and an unknown key each refused naming the key; `editorTrackingInfo` turned
tracking and the own-features rule on (`editFieldsInfo.creatorField` `created_user`,
`ownershipBasedAccessControlForFeatures` present) and off again. Conformance: `UpdateDefinitionTests`'
`The_typical_script_is_accepted_and_each_key_this_server_cannot_hold_is_refused_by_name` and
`Editor_tracking_is_turned_on_and_off_through_update_definition`.
