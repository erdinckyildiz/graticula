# ADR-288 — Offline sync is in: a hosted layer can be taken offline by Field Maps and Pro, and its edits come back

| | |
|---|---|
| **Status** | `REQUIRES PROTOTYPE` |
| **Confidence** | `MEDIUM` that the format can be written (§4: ArcGIS Pro reads and edits a file written without Esri's library) · `LOW` on what Field Maps uploads, which nobody here has seen yet |
| **Decided** | 2026-11-10, **by owner decision**, in four answers the same evening. Asked whether Field Maps support was partial and told the offline gap was ADR-082's, the owner said *"Çevrimdışını açalım"* ("let's open offline"). Asked how the mobile geodatabase — a format Esri does not publish — should be come by, the owner chose *"Gözleyerek türet"* ("derive it by observing") from three options (§2). Asked what the first version covers, the owner chose **on-demand offline areas and preplanned map areas both** — *"Bir de hazır harita alanları"*. Asked nothing, the owner then widened the test ground: *"test verisi açacaksak esri.aecon.com'u da kullanabilirsin. sorun yok"*. **Everything in §5 not traced to one of those four sentences is `INFERRED`**, listed in §11 and asked as [Q-233](../open-questions.md). |
| **Supersedes** | [ADR-082](ADR-082-offline-sync-is-not-in-v1.md) — *offline sync is not in v1* |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

ADR-082 kept offline sync out of v1 on two grounds: it is weeks of work, and *"none of it can be tested without the
clients on a device, which this project does not have today"*. Both of its revisit triggers have since fired. A device
exists — Field Maps 26.2 on an Android emulator signs in, collects and works tasks against this server (ADR-077
condition 5, ADR-286) — and the owner has asked for it.

ArcGIS takes a feature service offline through `createReplica`, `synchronizeReplica` and `unRegisterReplica` on the
service. Field Maps' *offline areas*, both the ones a worker draws on the phone and the *preplanned* ones an owner
defines on the map, and Pro's *Download Map*, are all built on them, and all of them ask for `dataFormat=sqlite`: a
**mobile geodatabase**, a SQLite file whose tables, item definitions and geometry encoding Esri does not document.

That last fact is why this ADR exists rather than a list of routes. A server cannot answer `createReplica` for these
clients without writing that file, and cannot accept their edits without reading what they send back.

## 2. Alternatives considered

The owner was asked how the format should be come by, with the finding that Esri publishes only the REST side.

### Alternative A — Derive the format by observation, and disclose it (chosen by the owner)

**Argument for.** It is the only way the clients the owner named work. The project already does it on the same terms:
ADR-286 learned Field Maps' task configuration from public files and wrote the derivation down, ADR-030 reads a
reference implementation under logged conditions. The observation here uses data this project made — geometries it
chose, written by ArcGIS Pro; a service it created, replicated by ArcGIS Server — so nobody else's content is read.

**Argument against.** It is an undocumented format, written by a server that is not Esri's. A change on Esri's side can
break it without notice, and it is this server, not the client, that would have to follow. §5 of CLAUDE.md forbids
*reproducing* undocumented internals, and a writer of this format reproduces a file layout.

### Alternative B — Serve only `dataFormat=json`

**Argument for.** JSON replicas are documented. Nothing undocumented is written. The ArcGIS API for Python and scripts
could use them.

**Argument against.** Field Maps and Pro do not ask for JSON. The owner's question was about Field Maps, and this answers
a different one.

### Alternative C — Research first, decide later

**Argument for.** Measure how much of the format can be reached from public sources before committing.

**Argument against.** The answer, measured the same evening, is *the varint family and nothing else* (research note §0),
so the decision would have come back to A.

## 3. Counterarguments to the preferred option

**"Reproduce" is the line, and this crosses it.** The strongest case against is CLAUDE.md §5 itself. The answer is the
one ADR-030 drew: the line is copying Esri's code or content, not knowing a file layout a client requires. No Esri
binary is linked or called by the server; no Esri-produced file is shipped; the statements that create the tables are
written here; the test vectors are this project's geometries. The derivation is in the open
([research/mobile-geodatabase-format.md](../research/mobile-geodatabase-format.md) §0) so that a reader can judge the
line for themselves. It is still the most exposed thing this server does, and §9 says what reopens it.

**It is the least tested surface in the server.** ADR-082 said so, and it is still true: no automated test here can run
Field Maps. Condition 1 makes the device measurement the gate, not the unit tests.

**Preplanned map areas double it.** They need map area items, packaging jobs and a registration path that on-demand
areas do not (§5.6), and the owner chose them anyway. The order in §5.7 puts them after the on-demand path works,
because they reuse every part of it.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Field Maps and Pro ask for `dataFormat=sqlite`; JSON is the other format | Esri REST API, *Create Replica*, *Synchronize Replica*, *Using sync* — "Runtime clients are designed to work where the data format is set to SQLite" | developers.arcgis.com (public), read 2026-11-10 |
| The geometry encoding is known exactly | 50 of 50 blobs — 47 written by ArcGIS Pro from input chosen here, 3 by ArcGIS Server 12.1 — decoded and re-encoded byte for byte by a disposable encoder | [research note](../research/mobile-geodatabase-format.md) §2 |
| A file written without Esri's library is read and edited by ArcGIS | Two captured files rebuilt with plain SQLite (Esri-module virtual tables written as schema rows, every geometry re-encoded); Pro listed every table, read every geometry, then inserted, updated and deleted, archiving included | research note §6 |
| A registered replica is archiving, the same shape ADR-078 keeps | `gdb_from_date`/`gdb_to_date` on every table; update closes and reopens; delete closes | research note §4; [ADR-078](ADR-078-a-hosted-layer-can-keep-its-history.md) §5.2 |
| A download delta is three kinds of row table under one change record | Two `synchronizeReplica` downloads after a server insert, update, delete and attachment | research note §5 |
| What Field Maps uploads | **Not measured.** It arrives at this server once `createReplica` is answered | — condition 1 |
| Enterprise hosted services refuse `perReplica` | *"Enterprise hosted feature services do not support replicas of type 'perReplica'"* — the service's own answer | Aecon production, 2026-11-10 |

## 5. Decision

1. **Offline sync is in** (owner). [ADR-082](ADR-082-offline-sync-is-not-in-v1.md) is superseded and v1-scope §3d is
   amended. A hosted feature service can be sync-enabled; one that is says `Sync` in its capabilities,
   `syncEnabled: true`, `syncCapabilities`, and `supportsDisconnectedEditing: true` on its layers.
2. **The mobile geodatabase is written by this server, from a derivation disclosed in the research note** (owner). The
   server never links, loads or calls Esri's library.
3. **Sync turns on the layer's history** *(INFERRED — §11.1)*. `createReplica` needs *what changed since generation G*,
   and [ADR-078](ADR-078-a-hosted-layer-can-keep-its-history.md)'s `<table>__history` answers it; ArcGIS turns on
   archiving when sync is enabled for the same reason. A server generation is epoch milliseconds, as ArcGIS's are.
4. **The first version answers** `createReplica` (`syncModel=perLayer` and `none`, `dataFormat=sqlite`, sync and async),
   `synchronizeReplica` (upload, download, bidirectional; `editsUploadID` from the service's uploads), `unRegisterReplica`,
   `replicas` and `replicas/{id}`. `perReplica` is refused as Enterprise refuses it; `dataFormat=json` is a later step
   *(INFERRED — §11.2)*.
5. **Conflicts: the last edit to reach the server wins**, per row, as ArcGIS's hosted services resolve them
   *(INFERRED — §11.3)*. A row deleted on the server and edited offline stays deleted.
6. **Preplanned map areas** (owner) — map area items related to the web map, packaged on the server as registered
   geodatabases and tile packages (the VTPK of ADR-098), downloaded by Field Maps and registered on first sync. Their
   exact REST shape is not yet measured and is condition 3.
7. **Order.** (a) the writer, tested against Pro; (b) `createReplica`/`unRegisterReplica` and the sync-enabled
   documents; (c) Field Maps on-demand download on the emulator — **condition 1 begins here**; (d) `synchronizeReplica`
   download, then upload, read from what Field Maps sends; (e) attachments both ways; (f) Pro *Download Map* and *Sync*;
   (g) preplanned areas; (h) Studio: *Enable sync* on a layer's settings, the replicas list, unregister.
8. **Hosted layers only** *(INFERRED — §11.4)*, as ADR-078's history is. A registered or file-based layer says
   `syncEnabled: false`.
9. **True curves and layers with more than one geometry column are refused** at `createReplica`, with a reason, until
   a sample says how they are written.

> **2026-11-10 — the server side of step (b) and (d) is built, on branch `offline-sync-server`; no condition is met by
> it, all of them need a device.** The routes are in `src/Graticula.Host/OfflineReplicaEndpoints.cs`: `createReplica`,
> `synchronizeReplica`, `unRegisterReplica`, `replicas`, `replicas/{id}`, `jobs/{id}` (the status URL) and
> `replicafiles/{id}` (the result, to its requester only), under every FeatureServer prefix; the writer is resolved from
> DI and a server without one answers 501 by name. Sync is switched by `POST /admin/services/{name}/sync` and by
> `updateDefinition`'s `syncEnabled` (or `Sync` in its capabilities); enabling turns on each layer's history, disabling
> unregisters the replicas, and turning a layer's history off turns its service's sync off (§11.1). Migration 137 adds
> `service.sync_enabled` and the `replica` registry (`PostgresOfflineSyncStore`). The documents say `syncEnabled`,
> `syncCapabilities` and `Sync` through `Graticula.Api.ArcGis/Offline/SyncDocument.cs`. A registered copy is the layer
> as it was at its generation (`historicMoment`); a download is the layer at the old and the new generation compared;
> an upload goes through the service's own `applyEdits` with `useGlobalIds=true`, the last edit winning and a server
> delete standing (§5.5). §5.9's refusal of true curves and of several geometry columns is **not** built: nothing here
> can detect a curve yet. What is left owing is D-399 (attachment changes are not downloaded), D-400 (replica jobs
> live in one process) and D-401 (a generation is a moment; an upload-only sync is echoed).

## 6. Consequences

**Positive.** The first question a field organisation asks has a yes. Field Maps' offline areas, preplanned areas and
Pro's Download Map can work against a Graticula server, and README's *No offline editing* limit can go when condition 1
is met.

**Negative.**

- An undocumented format is now this server's to keep up with. A Field Maps or Pro release can change what it accepts.
  §9's first trigger watches for it.
- History becomes mandatory for a sync-enabled layer, with ADR-078's write cost (measured there) on every edit.
- A new dependency: the writer needs SQLite with R*Tree, and nothing in the solution has it managed today
  *(INFERRED — §11.5)*.
- Replicas are durable state: a device that never syncs again leaves a registration and holds history back from
  being trimmed.

**Ports created.** The geometry blob is pure code and lives in the ArcGIS adapter (`Graticula.Api.ArcGis`,
`Offline/MobileGeodatabaseGeometry`), which keeps its rule of no package references. The file — tables, items,
rows — is written and read behind `IMobileGeodatabaseWriter` / `IMobileGeodatabaseReader`, declared in the adapter and
implemented in a new project, `Graticula.Offline.Sqlite`, the only one that references the SQLite library; no SQLite
type appears in a Tier 1 signature.

**State.** A replica registry in the catalogue (id, GUID, name, owner, layers and their server generations, created
and last synced), and the history tables of ADR-078. Migration number at implementation.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Field Maps accepts a mobile geodatabase written by a server other than ArcGIS's, as Pro does | **Unvalidated** — condition 1 |
| — | What Field Maps uploads has the delta shape of §5 of the research note | **Unvalidated** — condition 2 |

## 8. Dependencies

**Depends on:** [ADR-078](ADR-078-a-hosted-layer-can-keep-its-history.md) (history), [ADR-098](ADR-098-vector-tiles-can-be-exported-as-a-package.md)
(offline basemaps), [ADR-077](ADR-077-z-and-m-ride-beside-x-and-y.md) (Z and M), [ADR-286](ADR-286-field-maps-tasks-are-turned-on-in-studio.md)
(Field Maps on a device), [ADR-030](ADR-030-reading-the-reference-implementation.md) (disclosure).

**Depended on by:** —

## 9. Revisit triggers

- A Field Maps or Pro release refuses a file this server writes that the previous release accepted.
- Esri publishes the mobile geodatabase format — then the citation moves to it and §2A's argument against goes.
- The replica registry holds a replica unsynced for longer than the history it needs is kept.

## 10. Dissent

None recorded. ADR-082's reviewer called offline *"the first question a field organisation asks"*; this is the answer.

## 11. INFERRED — asked as [Q-233](../open-questions.md)

1. **Sync turns history on, and turning history off unregisters the layer's replicas** (§5.3).
2. **`perLayer` and `none` first; `dataFormat=json` later** (§5.4).
3. **Last edit wins, per row**; a server delete beats an offline edit (§5.5).
4. **Hosted layers only** (§5.8).
5. **SQLite through `Microsoft.Data.Sqlite` with the `e_sqlite3` bundle** (MIT and Apache-2.0, public-domain SQLite
   with R*Tree), confined to a new `Graticula.Offline.Sqlite` by an architecture test as DuckDB is to its provider —
   rather than GDAL's SQLite in the reader process, which would put a writer behind a process built to read untrusted
   input. `Graticula.Api.ArcGis` keeps its rule of no package references.

## 12. Conditions

1. **Field Maps takes an on-demand offline area of a Graticula map on the emulator**, collects offline with the network
   off, and syncs it back: rows, Z, attachments, and a server-side edit made meanwhile arriving on the phone.
2. **What Field Maps uploads is recorded** in the research note, from the file it sent, before the upload reader is
   written against it.
3. **Preplanned map areas** are measured on an Enterprise portal (their items, relationships and packaging), then
   served and downloaded by Field Maps.
4. **ArcGIS Pro's Download Map and Sync** work against a Graticula map.
5. **Conflicts** — the same row edited on the server and offline — resolve as §5.5 says, measured on the device.
6. **The writer's test vectors are this project's geometries**, and no file Esri wrote is committed.
