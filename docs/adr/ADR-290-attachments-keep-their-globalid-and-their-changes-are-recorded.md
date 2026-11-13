# ADR-290 — A hosted layer's attachments keep the GlobalID a device gave them, and their changes are recorded by the database

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the mechanism is tested against PostgreSQL (§4), and Field Maps 26.2.2 was seen on the emulator taking every attachment change §12.1 (a)–(e) names, both ways (2026-11-13). Not `HIGH`: §11.4's window is narrower than it reads (§12.1, *what the measurement found*), and nothing has run on a real phone |
| **Decided** | 2026-11-13, by the session that closed [D-399](../architecture-debt.md), on the design the task proposed. **Not an owner decision**; everything in §5 not traced to the code or to ADR-078/ADR-288 is `INFERRED` and listed in §11. |
| **Supersedes** | — (amends [ADR-288](ADR-288-offline-sync-is-in.md) §5.4: its *"attachments empty — the server's own attachment changes do not travel"* is no longer true of the mobile geodatabase download) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

ADR-288 §5.4 made `synchronizeReplica` a download of what changed in a layer since the replica's generation, read from the
layer's history (ADR-078): the features the history names, read as they were then and as they are now, and compared.
ADR-078's history records **rows**. It does not record attachments, so every download carried empty attachment inserts,
updates and deletes ([D-399](../architecture-debt.md)): a photo added, replaced or removed on the server after a worker took
an area offline never reached the phone, and the owner's own ADR-288 condition 1 (*attachments both ways*) was open on it.

The same row names a second gap. The attachment table (`<table>__attach`, ADR-013 §4) kept no GlobalID, so a replica was
given one derived from the layer and the attachment's id (`OfflineReplicaShapes.AttachmentGlobalId`). An attachment the
**device** creates arrives with a GlobalID of its own, is stored under a new integer id, and the server thereafter called it
by the derived one — so the device's later update or delete of it, which names the GlobalID it gave, was refused.

## 2. Alternatives considered

### Alternative A — Read the download from `uploaded_at` (rejected by D-399 itself)

**Argument for.** No schema change; `uploaded_at` is there. An attachment added since the generation has a later
timestamp.

**Argument against.** A delete leaves no row to carry a timestamp, so deletes could not be sent at all; an update sets
`uploaded_at = now()` in the *start* of its transaction and the bytes stream for longer, so an attachment can be stamped
before a generation and become visible after it, and is never sent. Guessing from timestamps is what the debt row rejects
by name: *"would send what the history cannot vouch for"*.

### Alternative B — Have the store write its own log in the same transaction

**Argument for.** No trigger, no function, nothing created in the schema beyond the log.

**Argument against.** ADR-005 §3.8 and ADR-078 §1 decided the opposite for rows, and for the same reason: anyone with
credentials writes the table directly, and a feature's deletion reaches its attachments through the foreign key's
`on delete cascade` without passing through this server at all. A log written by the server would be missing exactly the
changes made around it, and a cascade is the commonest of them.

### Alternative C — One log table per attachment table, a trigger function per table

**Argument for.** The shape ADR-078 chose for history (`<table>__history`, `<table>__history_fn`).

**Argument against.** Every place that drops, renames, recycles or copies a layer's tables names its companions one by one
(`PostGisImporter.DropAsync`, `Recycle`, collection views), and each would have to learn two more. A table left behind is a
log nothing reads; a function left behind outlives its trigger. One log and one function per **schema**, keyed by the
attachment table's name, leave at worst dead rows, which `DropAsync` removes, and drop with the schema.

### Alternative D — Change `IAttachmentStore`

**Argument for.** One interface.

**Argument against.** MySQL, Oracle and SQL Server implement it, none of them keeps a change record, and a new member
forced on four implementers (and any decorator — a default interface member is lost in a decorator, a trap this project
has fallen into) answers a question three of them cannot. A second, optional interface, `IAttachmentSync`, which only
PostGIS implements, says so honestly: the host asks `store is IAttachmentSync` and, without it, sends none.

## 3. Counterarguments to the preferred option

**One race is not closed.** A package's attachments, and those of a feature a download adds, are listed as they are when
they are read, not as they were at the generation (the store keeps no attachment versions, as the history keeps row
versions). An attachment written between the generation and that read is therefore in the package *and* named by the record
after the generation, and the next sync sends it again — an insert of a GlobalID the device already holds. The window is the
time a package takes to write; whether Field Maps takes the repeat quietly is condition 1(f).

A trigger and a function per schema are DDL a role that serves layers may not be allowed to run; the first upload to a
table made before this decision now alters it. A log that grows without limit is a table nobody prunes (D-421). Stamping
at commit with `clock_timestamp()` in a deferred trigger is a different rule from the one ADR-078 uses for row versions
(`now()`, the start of the transaction), and two rules for one generation is the kind of thing that confuses the next
reader. And a download that cannot vouch for a window sends nothing for it, which is what it did before — so for an old
replica the fix is invisible until its second sync.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| An attachment keeps the GlobalID it is stored with; the same GlobalID stored twice is one attachment | `PostGisAttachmentSyncTests.An_attachment_keeps_the_GlobalID_its_device_gave_it_…` | PostgreSQL 16.10 + PostGIS, 2026-11-13 |
| Insert, update, delete and cascade are recorded and read back net; an upload that rolls back leaves nothing | `…What_is_added_changed_and_removed_…`, `…A_feature_deleted_takes_its_attachments_…`, `…An_upload_that_is_refused_…` | same |
| A change is stamped when it commits, not when its transaction began | `…A_change_is_stamped_when_it_commits_…`: a 400 ms transaction, a window that closes inside it does not contain the change and the next one does | same |
| A table made before this decision is evolved in place, keeps every id and its absent GlobalID, and a replica older than the record is not vouched for | `…A_table_made_before_the_record_is_evolved_in_place_…` | same |
| A truncate is not vouched for | `…A_table_emptied_in_the_window_…` | same |
| The net-change rule | `AttachmentChangeLogTests` | `tests/Graticula.Core.Tests` |
| A kept GlobalID names an attachment whatever id the device holds; a derived one only with the attachment's own id | `OfflineReplicaTests` (four tests) | `tests/Graticula.Host.Tests` |
| **Not measured:** what Field Maps does with attachment inserts, updates and deletes in a delta | — | §12 |

## 5. Decision

1. **Each hosted layer's attachment table gains a nullable `globalid uuid`** and a unique partial index on it, added
   expand-only: `alter table … add column if not exists`, run by the first operation that writes the table (`EnsureTableAsync`)
   and by `PrepareAsync` before a replica is created. **NULL means *the GlobalID derived from the layer and the id***, which
   is what every replica already on a device was told, so no id and no GlobalID is ever rewritten and no replica breaks.
   A read of a table not yet evolved asks for the column, is told it is not there, and asks again without it; **a read runs
   no DDL**.
2. **An uploaded attachment insert is stored under the GlobalID the device sent** (`IAttachmentSync.AddAsync`); the same
   GlobalID stored again returns the first attachment's id, so an upload whose answer never arrived adds nothing twice.
3. **An uploaded update or delete names its attachment by that GlobalID** — a kept one finds it whatever id the device
   holds (a device-made attachment has an id of the device's own and another here); an attachment stored without one is
   found only by its derived GlobalID *together with* its own id, as before, so it cannot be reached by counting
   (`OfflineReplicaShapes.ResolveAttachment`). The attachments an upload names are added to the set the download leaves
   out, by GlobalID, as its rows already are.
4. **The database records every change to the table.** One log table per schema, `graticula_attachment_log`
   (`at`, `attach_table`, `kind`, `attachmentid`, `rel_objectid`, `globalid`), written by **one deferred constraint trigger
   per attachment table** (insert, update, delete; for each row) and a statement trigger for truncate, running one function
   per schema. **Deferred, so a change is stamped at commit** with `clock_timestamp()`, once per transaction: an attachment
   streams in chunks for as long as its bytes take, and one stamped at the start of its transaction could sit before a
   generation taken meanwhile and be sent by no download. The same transaction's rollback leaves no entry; a feature's
   deletion, which the foreign key cascades, is recorded as the deletes it is.
5. **A window is read as a net change per attachment**: the first entry says whether it existed at the start (an `insert`
   means it did not), the last whether it exists at the end (a `delete` means it does not). Added and removed inside the
   window is no change; written twice is one update (`AttachmentChangeLog.Net`).
6. **A window the record cannot vouch for is not guessed.** A table that held attachments before the record began says
   where the record begins (a `begin` entry written when it is evolved); a window starting earlier, a window containing a
   truncate (which names no attachment), or a table with no trigger answers `Unvouched`, and the download sends no attachment
   changes for it — what it sent before. A layer with no attachment table is vouched for and empty.
7. **The download.** For a layer with attachments, a replica whose `attachmentsSyncDirection` is `bidirectional` (or
   `download`) and a request with `returnAttachments`, `ChangesAsync` also sends: the attachment inserts, updates and
   deletes the record vouches for in `(generation, now]`, **of features the replica holds after the sync**; every
   attachment a feature has now, for each feature **the download adds** to the replica (which needs no record, and whose
   log entries are then left out so none is sent twice); and no attachment delete for a feature the replica loses, because a
   geodatabase's attachment relationship is composite and the device removes them with it (`INFERRED`, §11). Nothing the
   device itself uploaded in the same sync is sent back.
8. **`IAttachmentSync` is a second interface, implemented by PostGIS only** (§2 D). The three other providers keep
   deriving GlobalIDs and sending no attachment changes after creation, recorded as
   [D-420](../architecture-debt.md).
9. **A replica's creation prepares the tables first**, before its generation is taken (`createReplica` with
   `syncModel=perLayer`, and a map area's packaging), so the record begins before the replica does. A store that cannot be
   prepared (a role that may not alter the table) does not stop the replica; its first download says so by sending nothing.
10. **JSON replicas** (`dataFormat=json`) name each attachment by its kept GlobalID in `createReplica`; their `synchronizeReplica`
    download still carries no attachment changes ([D-409](../architecture-debt.md)), because ArcGIS's JSON shape for them has
    not been seen and nothing syncs by JSON.

## 6. Consequences

**Positive.** A photo added, replaced or removed on the server reaches the phone on its next sync; a photo taken on the phone
can be replaced or deleted from the phone afterwards; nothing is guessed from a timestamp; every existing replica keeps
working; and the record also answers a question the REST attachment endpoints could not (what changed, and when).

**Negative.** Every hosted layer with attachments gets a column, an index, a log row per attachment write, a trigger and a
function, and a role without DDL rights cannot give them (§3). The log is not pruned (D-421). A replica older than the
record gets nothing for attachments on its first sync after the upgrade. REST's `attachmentInfos` still answer
`globalId: null` while a replica says otherwise — a disagreement between two interfaces about one attachment that this
ADR leaves (D-422).

**State.** In the layer's own database, shared by every node and not in the catalogue: a `globalid` column and index on each
`<table>__attach` (and `layer_<id>__attach` in the datastore), one `graticula_attachment_log` table and one
`graticula_attach_log()` function per schema, and a trigger pair on each attachment table, all made by the first write to
the table or by `PrepareAsync` — no migration, because the tables are the layer's and are made lazily (ADR-013 §4). Nothing at
runtime: the log is read at each download.

**Ports created.** `IAttachmentSync` (Core), optional, implemented by `PostGisAttachmentStore`.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | None of the registered assumptions is touched. | — |

## 8. Dependencies

**Depends on** (upstream ADRs — if they change, this is reviewed): ADR-013 §4 (companion table), ADR-078 (history, and
the generation as a moment), ADR-207 (registered layers' attachments in the datastore), ADR-288 §5.4–5.5.

**Depended on by** (downstream ADRs — if this changes, review these): ADR-288 condition 1.

## 9. Revisit triggers

- Field Maps or Pro refuses a delta with attachment deletes of features that remain, or applies attachment inserts for an
  id it already holds (§12).
- The log of one layer passes a million rows (D-421 stops being a note).
- A provider other than PostGIS gains sync (D-420).

## 10. Dissent

None recorded; the design was proposed in the task and not argued with anyone. The owner has not seen it.

## 11. INFERRED — to be confirmed

1. ~~**A feature the replica loses takes its attachments with it**, so no attachment delete is sent for it. Reason: the
   attachment relationship class of a geodatabase is composite. Not measured on a device.~~ **Measured 2026-11-13 (§12.1 (e)):
   a feature deleted on the server went from the phone with its photo, the sync sent no attachment delete (`-0`), and Field
   Maps reported the sync complete.** No longer an inference.
2. **A feature the download adds is sent with every attachment it has now**, not only those the record names.
3. **`attachmentsSyncDirection` is `none`, `upload` or `bidirectional`** (ADR-288's request reading); the download carries
   attachments for `bidirectional`, and `download` is accepted too, as the task named it, though ArcGIS does not define it.
4. **A window the record cannot vouch for sends nothing**, rather than every attachment of every feature in the replica.
   The second would be correct and heavy; the first is what the server did ~~and loses nothing new~~. **It can lose
   something new, measured 2026-11-13:** ArcGIS Runtime moves a layer's generation only when a delta carries a
   `GDB_DataChangesDatasets` row for it, so a device whose layer saw no change keeps asking from the generation its package
   was written with. For a package older than the record, every window starts before the record began, is not vouched for,
   sends nothing, and so writes no row — and the device stays there until a *row* of that layer changes. A photo added on the
   server in that time (`server-added.png`, 11:08 UTC) never reached the phone. Packages written after the record began are
   not affected; a map area refreshed after upgrading is the way out for one that was. [D-423](../architecture-debt.md).
5. **Stamping at commit** is this ADR's choice and not ADR-078's.

## 12. Conditions

1. **Measure on the Field Maps emulator and on a device:** (a) a photo added on the server after the area was taken reaches
   the phone on *Sync now*; (b) one replaced on the server arrives changed; (c) one deleted on the server disappears while its
   feature stays; (d) a photo taken on the phone, synced, then replaced and deleted on the phone is applied by the server
   (the case the kept GlobalID exists for); (e) a feature deleted on the server does not make the sync fail on the attachments
   the phone still holds for it (§11.1); (f) the repeat described in §3 is taken quietly. **PARTLY DISCHARGED 2026-11-13** —
   (a)–(e) on the Field Maps 26.2.2 emulator against dev, on a preplanned area registered with `registerExistingData`, each
   read back from both sides (the phone's geodatabase pulled with `adb`, the server's table read):
   - (a) `server-photo-2.png` added on the server arrived on the next *Sync now*, under its feature (`attachments +1`).
   - (b) replaced on the server, it arrived as `server-photo-2-replaced.png`, 1525 → 1952 bytes, same GlobalID (`~1`).
   - (c) deleted on the server, it went from the phone and its feature stayed (`-1`).
   - (d) `phone_d.png` attached on the phone was stored on the server under the phone's GlobalID (`a8358440-…`); renamed on
     the phone, the server's row was renamed; removed on the phone, the server's row was deleted. Before this ADR the last
     two were refused.
   - (e) feature 6 deleted on the server, with `Photo 1.jpg` on the phone: the sync completed and both went (§11.1).
   - (f) is not measured: a lost answer cannot be made to happen from the emulator. The store's half is tested (§4).

   **What the measurement found besides.** The first attempt showed (a) failing, and the cause was §11.4's window, not this
   ADR's code: the phone asked from 1794474726446, its package's generation, sync after sync. `synchronizeReplica` now logs
   each layer's window and what it carried, which is how that was seen.
2. **Prune the log** with the history's keeping period, or state why not (D-421). **Open.**
3. **An end-to-end test of `synchronizeReplica` against PostGIS with an attachment**, which needs the host fixture
   and was not written here: the pieces are tested (§4), the glue between them in `OfflineReplicaEndpoints` is not. **Open.**
