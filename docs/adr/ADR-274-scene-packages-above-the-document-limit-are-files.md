# ADR-274 — A scene layer package above the document limit is a file in the server directories

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-07. **Where the packages live is the owner's decision** ([ADR-272](ADR-272-owner-decisions-on-3d-scope.md) §5.3: *"State volume + DB'de referans/hash"*). How backup, several nodes and deletion follow from it is this ADR's. |
| **Supersedes** | D-386's open half: *a package is a document, so it is bounded by the document limit*. |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

A scene layer package is a document ([ADR-269](ADR-269-scenes-web-scenes-scene-packages-and-generated-i3s.md) §5.2), held
in the store as one `bytea` and bounded by `Graticula:DocumentMaxMB` (64 MB by default). Esri's own sample packages are
far larger: the integrated mesh is 286 MB and the point cloud 578 MB. The upload also reads the whole file into memory,
and each node copies the package out of the store once, as one array, to its own disk to seek in. A 578 MB package would
be one 578 MB array on a host that measured 1.2 GB free beside the live showcase.

## 2. Alternatives considered

### Alternative A — the server directories, with a reference and a hash in the store (chosen; the owner's)

A package above the document limit is streamed to `{server directories}/scene-package-files/{sha256}.slpk` and never
held in memory. The document row keeps the file's relative path, its SHA-256 and its size, and no bytes. The server
directories are ADR-012 phase 1's: the state directory on a node of its own, and the one path every node mounts on a
site. So a package uploaded through one node is read by the others, and a node whose mount is missing is refused at
start, as it already is for imagery.

### Alternative B — raise the document limit

**Rejected by the owner.** It keeps the whole package in memory, twice: once at upload and once on each node's first
read.

### Alternative C — every scene package a file, whatever its size

One rule is simpler. But it moves packages that work today out of the store, its backups and its transactions, for no
reader's gain. **Rejected for now**; it is a revisit trigger.

## 3. Counterarguments to the preferred option

- **Two places for one kind of document.** Every reader of a package goes through one method that asks which it is, so
  the two do not spread.
- **A file and a row can disagree.** The row is written only after the file is complete and named by its hash, and a
  row whose file is missing answers *not found* with the reason. Nothing is served from a partial file. A file no row
  names — no live document of any organisation, no copy in any recycle bin — is removed once it is a day old (§5.4).

## 4. Evidence

- `ScenePackageFilesTests` (host) cover the hash naming, a second upload of the same bytes sharing one file, the path
  rule (a recorded name never leads outside the folder), and the sweep of partial uploads.
- **Esri's whole integrated mesh sample (`Rancho_Mesh_v18.slpk`, 285,961,695 bytes) on the fixture, 2026-11-07, with
  the default 64 MB document limit**, used locally and not committed:
  - The upload was kept as a file and recorded. The server's resident memory went from 192 MB to a peak of 227 MB
    while it was uploaded, not by the package's size.
  - The service, layer and node pages were served from the file, with no copy made on the node.
  - The download had the same SHA-256 as Esri's file, and memory stayed at 228 MB.
  - The Maps SDK drew the whole mesh, textured.
  - The first attempt was refused by migration 121's `document_size_matches` check (a document's size is its
    bytes'). That is why migration 134 widens it, and adds `document_file_is_whole`.

## 5. Decision

1. **Above `Graticula:DocumentMaxMB` and up to `Graticula:ScenePackageMaxMB` (default 4096), a `.slpk` is a file.** It is
   streamed to a partial file while its SHA-256 is computed, moved to its hashed name, checked to be a zip with a layer
   document, and only then recorded. Anything else above the document limit is refused as before.
2. **The row** (`document.content_file`, `document.content_sha256`, migration 134) names the file relative to the
   server directories, never absolutely, so a site's nodes and a restored backup read the same name.
3. **Readers.** `ScenePackages` seeks in the file itself, with no copy. The item's download streams it. Everything that
   reads a document's bytes into memory gets *null* for such a document, which means "not here", and each of those
   paths is one that never takes a scene package (§6).
4. **Deletion.** ~~Only partial uploads are swept for now; a complete file is never deleted by this server yet.~~
   *(Superseded 2026-11-07 by condition 4's sweep, and this item corrected 2026-11-20, when it still said otherwise.)*
   `ScenePackageFileSweep`, hourly and once per site, removes a partial upload older than a day and a complete file
   older than a day that nothing names. Whether a row still names it is asked of every tenant's rows (row-level
   security, ADR-204) and of the recycle bin's copies (ADR-203), on the store's root with no tenant set, because a wrong
   answer loses a package. Two documents with the same bytes share one file, named by its hash, so the file is not the
   row's to delete alone: it goes when the last row naming it does.
5. **Backup** copies `scene-package-files/` beside the imagery, in `state/`, and restore puts it back.
6. **Replace file** on a package above the limit ~~is refused with the reason, for now~~ takes the upload's streaming
   path — kept as a file under its hash, never held in memory — and the row then names the new file; a small file
   replacing a large package goes back into the store and the row stops naming the file. The console's Replace file and
   the portal's item `update` (Pro's overwrite) both take it. *(Changed 2026-11-20, §9 condition 3.)* The replaced
   package's file is left to the sweep (item 4).

## 6. Consequences

- D-386 is closed in its storage half.
- The paths that read a document into memory: append, overwrite, item export, the content adds, the admin download and
  the portal's data. Each was read on 2026-11-07. None takes a scene package except the two download paths, and those
  now stream it.
- **State.** One more server directory, `scene-package-files/`. It is shared on a site (ADR-012 phase 1), in the backup, and
  swept.

## 7. Assumptions this decision rests on

- The server directories have room. A package is bounded by `ScenePackageMaxMB`, and the disk is the operator's.

## 8. Dependencies

ADR-012 (server directories), ADR-193 (backups), ADR-269, ADR-272 §5.3.

## 9. Conditions

1. **A site of two nodes reads a package uploaded through the other**, as `ASiteSharesItsServerDirectoriesTests` shows
   for imagery. *(Discharged 2026-11-07:
   `ASiteSharesItsServerDirectoriesTests.A_large_scene_package_uploaded_through_one_node_is_served_by_the_other` uploads
   a 65 MB package through one node and finds it under the shared directories and served by the other.)*
2. **A package of hundreds of megabytes is served without the host's memory growing by its size**, measured on Esri's
   integrated mesh sample. *(Discharged 2026-11-07: §4. The upload peaked 35 MB above idle, and the download added
   nothing.)*
5. **A console test of the large path**, on a fixture started with a small `DocumentMaxMB`, so CI keeps what §4
   measured by hand. *(Discharged 2026-11-07, without changing the limit: `LargeScenePackagesTests` makes its own 65 MB
   package, uploads it past the default limit, reads the scene service from it and downloads it with the same SHA-256.)*
3. **Replace file for a large package**, through the same streaming path. **DISCHARGED 2026-11-20:** the console's
   `PUT /content/documents/{id}/data` and the portal's `items/{id}/update` both stream a `.slpk`/`.3tz` above the
   document limit through the one `AdminEndpoints.KeepLargePackageAsync` the upload uses, and the store's new
   `ReplaceFileAsync` names it. Replacing such a package with a small file used to fail outright: the row kept naming
   the old file while taking bytes, which `document_file_is_whole` refuses, so the replace answered 500; `ReplaceAsync`
   now clears the name. `LargeScenePackagesTests.Replace_file_takes_a_package_above_the_document_limit_and_back_and_the_portal_update_does_too`
   replaces a small package by a 65 MB one, that by a small one, and that by a 65 MB one through the portal, and checks
   the scene layer and the download's SHA-256 each time. **Measured on the local fixture with a generated 1,610,613,150-byte
   package**, sampled every 100 ms: the console's Replace file took 21 s, and the host's working set went from 266 MB
   to a peak of 271 MB (private bytes +5 MB); the portal's update took 18 s and added 17 MB. The download had the
   package's SHA-256 and the scene layer read the new package.
4. **A file no live row, tenant or recycle bin entry names is removed**, with a test across two tenants and a restore
   from the bin. *(Partly discharged 2026-11-07: `ScenePackageFileSweep`, hourly and once per site, removes a
   day-old file that no document of any organisation and no recycle bin copy names. With no tenant set, the store's
   floor `graticula_tenant() is null` reads every organisation's rows, and the recycle bin keeps the document row whole,
   `content_file` included. `ScenePackageFilesTests` covers the rule. A test across two real tenants and a restore
   from the bin is still owed.)* **DISCHARGED 2026-11-20:**
   `ScenePackageFileNamesAcrossTenantsTests.Every_organisations_live_and_recycled_package_is_named_a_restore_keeps_it_and_a_purge_lets_it_go`
   (Platform.Postgres) makes the documents of two organisations through a role that neither is a superuser nor
   bypasses row-level security, routed by tenant as the host routes — and checks the floor binds, each organisation
   listing only its own. The sweep's names, read on the root, are both organisations' files; a file two organisations'
   rows share stays named when one deletes its row; a package in one organisation's recycle bin stays named and is not
   the other's to see; restored from the bin it names its file again with its size; recycled and purged, it is no
   longer named, and when the last row of the shared file goes nothing is. The folder half — a day-old file the names
   leave out is removed and a named one never — is `ScenePackageFilesTests`'.

## 10. Revisit triggers

- A package below the limit is wanted out of the store too (backup size, store growth): then Alternative C.
- Server directories on an object store (ADR-012's deferred adapter).
