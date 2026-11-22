# ADR-311 — Extract Changes on hosted layers that keep their history, and a webhook's `changesUrl`

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the operation's parameters and its answer's keys are ArcGIS's public reference; the difference itself is the one offline sync has computed since ADR-288 and is tested against PostgreSQL. the ArcGIS API for Python has called it against a local fixture (condition 2), and no webhook integration has followed a `changesUrl` yet, and eleven points the reference does not settle are `INFERRED` (§5.12) |
| **Decided** | 2026-11-22. The owner, on Enterprise 12.2 band 2 group 1 (*field work and editing*), answering *"extractChanges"* in a question dialog: **"Hosted + webhook"** — serve `extractChanges` on hosted layers that keep change history, and make the webhook payload's `changesUrl` a real `extractChanges` address. Everything below that the owner did not say is this session's, and what public documentation does not settle is `INFERRED` (§5.12) |
| **Supersedes** | — (amends [ADR-266](ADR-266-a-webhook-tells-another-system-what-changed.md) §5.3 and §5.10 Q2; ends the `extractChanges` half of [ADR-082](ADR-082-offline-sync-is-not-in-v1.md)'s exclusion, which [ADR-288](ADR-288-offline-sync-is-in.md) left standing) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

ArcGIS's **Extract Changes** (`FeatureServer/extractChanges`) answers *what changed in these layers between two server
generations*: the features inserted, updated and deleted, or only their ids, or only the extent they cover, with their
attachments. A service offers it when `ChangeTracking` is in its capabilities. It is what an integration polls instead
of re-reading a layer — ArcGIS Data Pipelines, FME, a script on the ArcGIS API for Python
(`FeatureLayerCollection.extract_changes`) — and it is what a feature service webhook's payload points at: ArcGIS's
`changesUrl` is an `extractChanges` request against the generations around the edit that fired it.

Graticula did not serve it on a feature service. [ADR-082](ADR-082-offline-sync-is-not-in-v1.md) kept it out with
offline sync; [ADR-288](ADR-288-offline-sync-is-in.md) brought sync in and said nothing about `extractChanges`;
[ADR-266](ADR-266-a-webhook-tells-another-system-what-changed.md) §5.3 therefore left `changesUrl` out of the webhook
payload and put object ids and a `query` address in its place. Only a 3D object layer's mesh answered it
([ADR-280](ADR-280-3d-object-layers-hold-meshes.md), `AdminEndpoints.Meshes.cs`), from its own edit stamps. The 12.2
comparison ([enterprise-12-2-gap.md](../research/enterprise-12-2-gap.md)) lists *Extract Changes with `layerQueries` and
`geometry`* as missing.

What makes this cheap is that the difference is already computed. A registered replica's download is *the layer at the
replica's generation compared with the layer now*, both read with `historicMoment` over the history a hosted layer keeps
([ADR-078](ADR-078-a-hosted-layer-can-keep-its-history.md)), with the features the history names since the generation as
the candidates (`OfflineReplicaEndpoints.ChangesAsync`, ADR-288 §5.4), and its attachments come from the attachment
table's own record of changes (ADR-290). `extractChanges` is the same question asked without a replica.

## 2. Alternatives considered

### Alternative A — A second change log for `extractChanges`

A table that every edit appends to — layer, object id, kind, generation — read by `extractChanges` directly.

**Argument for.** A generation could be a sequence number taken at commit, not a moment, which is what ArcGIS's
geodatabase generations are, and D-401's race (§5.9) would not exist for this operation. An ids-only answer would be one
index scan.

**Argument against.** It is a second record of the same facts that ADR-078's trigger already keeps, and a second place
that must see every write — including the writes that come straight to the database, which the history's trigger sees
and a log written by this server would not. It would also give `extractChanges` and `synchronizeReplica` two different
answers to one question. Not chosen; the brief that started this work says the same in as many words.

### Alternative B — `changesUrl` pointed at `query`

ADR-266 Q2's rejected alternative: keep no `extractChanges`, point `changesUrl` at the layer's `query` for the ids.

**Argument against.** A client that parses an `extractChanges` answer misreads a `query` answer, which is why ADR-266
refused it, and the owner's answer is to serve the real thing. Not chosen.

### Alternative C — `ChangeTracking` derived from the history, with no switch

A hosted service whose every layer keeps its history says `ChangeTracking`, with no stored flag.

**Argument for.** No migration; what is advertised can never disagree with what exists.

**Argument against.** ArcGIS turns change tracking on and off by the capabilities string, and its create-service
parameters can ask for it before the service has a layer — an intent that has to be kept somewhere until layers arrive.
A derived flag would also start advertising an operation the moment somebody turned history on for its own sake, and
could not be turned off except by deleting the history. Not chosen: the switch is stored (§5.1), and history remains the
condition for honouring it.

### Alternative D — Chosen: one switch, the replica machinery, ArcGIS's parameters

§5.

## 3. Counterarguments to the preferred option

- **A generation is a moment, not a transaction** (D-401). A transaction that began before a generation and committed
  after the read at that generation is in neither side's difference, and a client that polls from generation to
  generation never sees it. A change log with commit-time sequence numbers would not have this hole. §5.9 says how far
  it reaches and why it is accepted here as it was for sync.
- **Two reads of the layer per request.** The difference reads every candidate feature twice (as it was and as it is).
  A layer edited heavily since the asked generation is an expensive question. The candidates are only the features the
  history names in the window, which bounds it by the edits rather than the layer, and a window so old that the history
  no longer reaches it is refused (§5.5).
- **Sync and change tracking together.** ArcGIS's reference says a hosted feature service cannot have both. This server
  lets it (§5.10), and a client that reads both flags may behave in a way nobody has seen.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The parameters, their defaults and the capabilities that advertise them | *Extract Changes (Feature Service)*, read 2026-11-22: `layers`, `layerQueries`, `geometry`, `geometryType`, `inSR`, `outSR`, `serverGens`, `layerServerGens`, `returnInserts`/`Updates`/`Deletes`, `returnIdsOnly`, `returnExtentOnly`, `returnHasGeometryUpdates`, `returnAttachments`, `returnAttachmentsDataByUrl`, `dataFormat`, `changesExtentGridCell`, `fieldsToCompare`, `returnDeletedFeatures`; `extractChangesCapabilities` and its members; `changeTrackingInfo` | https://developers.arcgis.com/rest/services-reference/enterprise/extract-changes-feature-service/ |
| `serverGens` are epoch milliseconds in the reference's example, one value or a `[min, max]` pair | the same page: `"serverGens": [1653608093000, 1653608948000]` | same |
| `deleteIds` are GlobalIDs; `objectIds.deletes` are object ids; `fieldUpdates` is a list of object ids beside them; `hasGeometryUpdates` is per layer | the same page, its examples two to five | same |
| A client whose generation is below `minServerGen` cannot extract changes and must query the layers in full | the same page, `layerServerGens` | same |
| `ChangeTracking` is turned on and off by the service's `capabilities` through `updateDefinition` | the same page, *Notes* | same |
| A webhook's `changesUrl` is `…/FeatureServer/extractChanges?serverGens=[a,b]&async=true` with `return*=false` for the kinds the event is not | *Webhook payloads (Feature Service)*, four examples | https://developers.arcgis.com/rest/services-reference/online/web-hooks-payloads-feature-service/ |
| The difference between two moments of a hosted layer, with attachments, is computed and tested | ADR-288 §5.4, ADR-290; `AnUploadOnlySyncIsNotEchoedTests`, the sync tests | this repository |

Not read: the reference gives no example of a `returnExtentOnly` answer, no field list for an attachment in an
`extractChanges` answer, and no statement of what an expired `serverGens` answers. Each is `INFERRED` in §5.12. No
ArcGIS install file was read (clean room, CLAUDE.md §5).

## 5. Decision

### 5.1 `ChangeTracking` is a stored switch on a hosted service, honoured only where every layer keeps its history

A service row carries `change_tracking` (migration 145). It may be on only where sync may be on — hosted, not a view,
with layers that have an integer object id (`SyncFlags.Refusal`, ADR-288 §5.8) — because both stand on ADR-078's
history. **Turning it on turns history on** for every layer and table (as `syncEnabled` does, ADR-288 §11.1), and a
layer added later to a service with the switch on gets its history when it is added. **It is advertised only while
every layer still keeps its history**: a trigger dropped in `psql` takes `ChangeTracking` out of the documents and
`extractChanges` refuses by name, rather than answering from a history that stopped.

### 5.2 The doors are ArcGIS's

- **`updateDefinition`'s `capabilities`** with `ChangeTracking` turns it on; a `capabilities` string without it turns it
  off. Turning off destroys nothing — the history is kept, and so is every other use of it — so absence means off, as
  the reference says. (This is unlike `Sync`, whose absence leaves sync as it is, because turning sync off unregisters
  replicas.)
- **The portal's `createService`** with `ChangeTracking` in `createParameters.capabilities` sets the switch on the new,
  empty service; its layers get history as `addToDefinition` adds them. A view asks for it in vain, as before: it is
  left out of the offer.
- **`addToDefinition`** adds layers, and a layer added to a tracked service gets history then.

### 5.3 A layer without history is given one, not refused (`INFERRED`)

Asking for `ChangeTracking` on a service whose layers keep no history turns their history on. The reference does not
settle it: Enterprise geodatabase services turn change tracking on by themselves when the data is archived, and hosted
services turn it on by a switch. History is the mechanism here, so the switch that needs it starts it, as sync's does.
It is refused by name only where history cannot be had: a registered layer, a view, a layer without an integer object
id, and (at `extractChanges`) a layer whose document has no GlobalID field, because deletes are named by GlobalID.

### 5.4 What `extractChanges` takes

Served at `FeatureServer/extractChanges`, GET or POST, beside `createReplica`, through the same feature source,
filters and history reads as a replica download (`OfflineReplicaEndpoints`, a new partial file):

| Parameter | Here |
|---|---|
| `layers` | Layer and table ids, `0,1` or `[0,1]`. **Omitted: every layer and table** (`INFERRED` from ArcGIS's own `changesUrl`, which sends none). |
| `serverGens` | One generation (changes since it, to now) or `[min, max]`. |
| `layerServerGens` | `[{id, serverGen}]` per layer, `minServerGen` read and ignored. One of the two is required. |
| `layerQueries` | `queryOption` (`useFilter`, `all`, `none`), `where`, `useGeometry`, read by the replica parser. A layer not named is every change in the geometry. `includeRelated: true` is **refused by name** (D-469). |
| `geometry`, `geometryType`, `inSR` | An envelope (JSON or four numbers) or a polygon. A point, multipoint or polyline is **refused by name** (D-469). |
| `outSR` | Any reference the datastore knows. |
| `returnInserts`, `returnUpdates`, `returnDeletes` | Default true. |
| `returnIdsOnly` | `objectIds: {adds, updates, deletes}` per layer. |
| `returnExtentOnly` | The envelope of the changed features — added and updated as they are, deleted as they were — as `extent` (`INFERRED`, §5.12). |
| `returnHasGeometryUpdates` | `hasGeometryUpdates` per layer; refused with `returnUpdates=false`, as the reference says. |
| `fieldsToCompare` | `{"fields": [...]}`; `fieldUpdates`, the object ids of updates that changed one of them. Refused with `returnUpdates=false`. |
| `returnDeletedFeatures` | Deleted features whole, as they were, under `features.deletes`; ignored with `returnIdsOnly=true` or `returnDeletes=false` rather than refused, because the ArcGIS API for Python 2.4.3 sends it with every `extract_changes`, ids only included (measured 2026-11-22, condition 2). |
| `returnAttachments`, `returnAttachmentsDataByUrl` | Default true. Attachment changes from the attachment table's record (ADR-290), `adds`/`updates` with a `url` to the layer's own attachment route or base64 `data`, and `deleteIds` as GlobalIDs. Not in an ids-only or extent-only answer. A window the record cannot vouch for is **refused by name** rather than re-sent whole as sync does (D-423 does not apply: there is no device to move on). |
| `dataFormat` | `json`. `sqlite` (the reference spells it `sqllite`) is **refused by name** (D-469): the mobile geodatabase writer writes a replica's delta, keyed by a replica, and is not cheap to bend. |
| `changesExtentGridCell` | `none`. Any other is **refused by name** (D-469). |
| `async` | `true`: a job, answered by `statusUrl`, its result a JSON file (§5.7). Otherwise the answer itself (`INFERRED`, §5.12). |

Anything else ArcGIS documents and this list does not serve is refused by name, and none of it is advertised.

### 5.5 Generations

A server generation is **a moment in epoch milliseconds**, as a replica's is (ADR-288) and as the reference's example
is. The answer's `layerServerGens` gives each layer `{id, minServerGen, serverGen}`: `serverGen` is the window's end
(the pair's `max`, or now), `minServerGen` the earliest moment the layer's history answers for (its
`archivingInfo.startArchivingMoment`, ADR-078). **A generation below `minServerGen` is refused** with 400 and a sentence
that says to query the layer in full, which is what the reference tells such a client to do; the code is `INFERRED`.
`min` above `max` is refused; a `max` in the future is read as now and answered as now (`INFERRED`).

### 5.6 What it answers

- **Ids only:** `{layerServerGens, edits: [{id, objectIds: {adds, updates, deletes}, fieldUpdates?, hasGeometryUpdates?}]}`.
- **Features:** `{transportType: "esriTransportTypeEmbedded", responseType: "esriDataChangesResponseTypeEdits",
  layerServerGens, edits: [{id, features: {adds, updates, deleteIds | deletes}, attachments?: {adds, updates, deleteIds},
  fieldUpdates?, hasGeometryUpdates?}]}`. A feature is `{geometry, attributes}` in `outSR` or the layer's reference,
  dates in epoch milliseconds and GlobalIDs braced, exactly as a JSON replica writes them (`OfflineJsonReplica`).
  `transportType` is embedded for the answer itself and `esriTransportTypeUrl` for a job's file (`INFERRED`).
- **Extent only:** `{layerServerGens, extent}` (`INFERRED`).

An update is a feature present at both moments whose row differs; a feature that left the filter is a delete and one
that entered it an insert, as in a replica download.

### 5.7 Asynchronous requests use the replica jobs

`async=true` starts a job in the replica job registry (`ReplicaJobs`, ADR-288 §5.5) and answers
`{statusUrl: …/FeatureServer/jobs/{id}}`; the status answers `responseType: "esriDataChangesResponseTypeEdits"` and,
when complete, `resultUrl` to the JSON file. **A job belongs to a signed-in caller**, as every replica job does, so an
anonymous `async=true` is refused by name and told to send `async=false` (`INFERRED`).

### 5.8 Who may ask

Whoever may read the layers: the service's read governance (`Governed(ByService)`), and `Query` in the caller's own
capabilities string — an add-only view could not have been tracked anyway. The rows are read through the same feature
source a query reads, so what a reader may not see in a query they do not see here.

### 5.9 How it stands on D-401

A history row is stamped with its transaction's start. A read at generation *g* sees every transaction that committed
before the read and began at or before *g*; a transaction that began before *g* and committed after the read is invisible
to that read, and the next request from *g* skips it because its stamp is not after *g*. **`extractChanges` inherits the
race exactly as sync has it, and nothing here fixes it** — the lead is measuring D-401, and the fix that would close it
(an older generation, overlapping windows) changes what a client sees for both operations at once and belongs there.
Two things narrow it for this operation. A webhook's `changesUrl` takes its window's start before the edit's transaction
began and its end after it committed (§5.11), so the change that fired it is always inside. And a client that asks with
an explicit `[min, max]` in the past sees every transaction committed by the time it asks. D-468 records the residue
against D-401's trigger.

### 5.10 Sync and change tracking may both be on (`INFERRED`)

The reference says a hosted feature service cannot have both. Here both read one history and neither interferes with
the other, and refusing one would make an owner choose between Field Maps' offline areas and a webhook's `changesUrl`.
Both are allowed; a service document with both says both.

### 5.11 A webhook's `changesUrl` is real where the service tracks changes

An edit raised to a feature service's webhooks records, beside its object ids, `serverGens = [g0, g1]`: `g0` the moment
before the edit's transaction began (floored to the millisecond), `g1` the moment after it committed (rounded up).
An attachment's event takes `g0` when the attachment request was resolved. When the service has change tracking on,
the payload carries

`changesUrl = …/FeatureServer/extractChanges?layers=[n]&serverGens=[g0,g1]&async=true&returnAttachments=false&…`

with the `return*=false` flags ArcGIS's examples give for each change type — `FeaturesCreated` without updates and
deletes, `FeaturesUpdated` without deletes, `FeaturesDeleted` without inserts and updates, an attachment's event without
features — and, for an edit that was several, only the flags all of its kinds give. **`layers=[n]` and `f=json` are
this server's additions** (`INFERRED`): ArcGIS's examples omit `layers`, which here means every layer, and the event is
about one. Where the service does not track changes, `changesUrl` stays absent and the payload is ADR-266 §5.3's, which
remains in every payload either way: `objectIds`, `layerUrl` and `queryUrl` are kept beside `changesUrl`, so no
receiver built on them breaks.

### 5.12 `INFERRED`, for the owner to confirm

1. A layer without history is given one when change tracking is asked for (§5.3), rather than refused.
2. `layers` omitted means every layer and table (§5.4).
3. `returnExtentOnly`'s key is `extent`, one envelope across the asked layers (§5.4, §5.6).
4. Without `async=true` the answer is the changes themselves, even with `returnIdsOnly=false`, which the reference says
   is asynchronous; ArcGIS's own `changesUrl` says `async=true` explicitly, and a client that never polls keeps working.
5. A generation below `minServerGen` is refused with 400 (§5.5).
6. A `max` in the future is read as now (§5.5).
7. An attachment in an answer has the JSON replica's fields — `attachmentId`, `globalid`, `parentGlobalid`,
   `contentType`, `size`, `name`, `url` or `data` (§5.4).
8. A window the attachment record cannot vouch for is refused, not re-sent (§5.4).
9. Asynchronous requests are for signed-in callers (§5.7).
10. Sync and change tracking may both be on (§5.10).
11. `changesUrl` adds `layers=[n]` and `f=json` to ArcGIS's shape (§5.11); `transportType` is embedded for an answer
    and URL for a job's file (§5.6).

### 5.13 What does not change

ADR-266's other payload fields, its signature and its outbox. The 3D object layer's own `extractChanges`
(ADR-280), which answers from its mesh stamps at its own route. `currentVersion` (ADR-181). Registered layers keep no
history and so cannot be tracked.

## 6. Consequences

**Positive.** An integration can poll a hosted layer for its changes the way it polls ArcGIS, and a webhook receiver
can follow `changesUrl` and get the change that fired it, in ArcGIS's shape. No second change record: replica downloads
and `extractChanges` give one answer to one question.

**Negative.** D-401's race reaches a polling client (§5.9, D-468). Each request reads the candidate features twice.
`dataFormat=sqlite`, `includeRelated`, point and line filters and grid cells are refused (D-469). Studio has no switch
for it: an owner turns it on through `updateDefinition` or `createService` (D-470). One migration (145).

**State.** One column in the catalogue, `service.change_tracking` (migration 145), shared by every node. The history it
stands on is ADR-078's, in each layer's datastore. An asynchronous request's job and its file are node-local, as every
replica job is (ADR-288 §5.5): a status URL is answered by the node that ran it, and not after its restart.

**Ports created.** None; no dependency is adopted.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The application's clock and the database's agree to the millisecond, so a generation taken on one compares with a history stamp taken on the other. ADR-288's generations assume the same. | Unmeasured |

## 8. Dependencies

**Depends on:** [ADR-078](ADR-078-a-hosted-layer-can-keep-its-history.md) (the history),
[ADR-288](ADR-288-offline-sync-is-in.md) (the difference, the filters, the jobs), [ADR-290](ADR-290-attachments-keep-their-globalid-and-their-changes-are-recorded.md)
(attachment changes), [ADR-266](ADR-266-a-webhook-tells-another-system-what-changed.md) (the payload it amends).

**Depended on by:** —

## 9. Revisit triggers

- D-401 is repaid or its fix changes what a generation is: this operation's windows change with it.
- An ArcGIS client is seen sending `dataFormat=sqlite`, `includeRelated` or a point filter here.
- A client misbehaves on a service that says both `Sync` and `ChangeTracking`.

## 10. Dissent

None recorded. The case against §5.10 is in §3 and is the reference's own sentence.

## Conditions

1. **Conformance tests against a running server** cover insert, update and delete across two generations, ids only,
   `layerQueries` with a `where`, attachments, an expired generation and a refusal; a webhook test follows `changesUrl`
   and gets the change that fired it; host tests cover the parameter reader and the payload's URL. **DISCHARGED
   2026-11-22** (branch b2-xchanges): `ExtractChangesTests` (six tests — the switch and the documents; inserts, updates and
   deletes across two generations with the `[min, max]` window, ids only, `fieldsToCompare`, `hasGeometryUpdates` and
   deleted features; `layerQueries` with a where and an envelope; an attachment by address and inline; an expired
   generation and seven refusals by name; an asynchronous job), `WebhooksTests.A_tracked_services_webhook_carries_a_changes_url_that_returns_the_change_that_fired_it`
   and `UpdateDefinitionTests`, 17 of 17 against a local fixture; `ExtractChangesUnitTests` (10) in the host tests. The
   replica downloads that share `WindowAsync` still pass (`AnUploadOnlySyncIsNotEchoedTests`, `ASyncOfAnAttachmentTests`
   and three more, 6 of 6). The whole conformance suite: 985 of 1,026, and the same 41 fail on the base commit 0bd8450a
   against a fixture seeded the same way (corpora outside the tree, Linux-only tool fences, PROJ grids).
2. **An ArcGIS client calls it.** The ArcGIS API for Python's `extract_changes`, or a Power Automate or Make flow
   following a webhook's `changesUrl`, is pointed at this server and what it did is recorded here. **DISCHARGED
   2026-11-22** with the ArcGIS API for Python 2.4.3 against the local fixture, signed in by name and password:
   `manager.update_definition` with `ChangeTracking` turned it on and `properties.capabilities` then said it;
   `extract_changes(layers=[0], servergen=[g], return_ids_only=True)` and the same with features answered the feature
   added through `edit_features`. Its first call was refused: the client sends `returnDeletedFeatures=true` with every
   request, ids only included, which this server refused as the reference's rule says; it is now ignored where it
   cannot apply (§5.4), and the second run answered. No webhook integration (Power Automate, Make) was tried.
3. **The owner confirms or changes the eleven `INFERRED` points of §5.12.**
4. **When D-401 closes**, this operation's window is re-read against the fix, and D-468 closes or is restated. **DISCHARGED 2026-11-22**: D-401 closed by closing each generation at the oldest open writer, and `extractChanges`' *now* is taken the same way (`OfflineReplicaEndpoints.ClosedGenerationAsync`); D-468 is repaid with it, and the repeat it allows is stated there.
