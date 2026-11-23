# ADR-317 — A backup is validated and restored through the admin API

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-23, by owner decision — *"Validate + API'den restore"* (validate, and restore from the API), the second answer of band 2 group 2's question dialog, on the recommendation that every node of the site goes into maintenance, the backup is restored into a new database, the server switches to it when it is done, and the old database is kept for a way back. The details below §5.1 are the dr agent's and are marked `INFERRED` where neither Esri's public documentation nor an earlier ADR settles them. |
| **Supersedes** | [ADR-193](ADR-193-backup-and-restore-are-the-server-s-own-commands.md) §5.5, *"restore runs only from the command line, against a stopped server"* — reopened by the owner; the command line keeps working |
| **Superseded by** | — |

---

## 1. Context

[ADR-193](ADR-193-backup-and-restore-are-the-server-s-own-commands.md) built backup as the server's own command and an
admin API (take, list, download, delete, a schedule), and kept restore off the API on purpose: *"it replaces the
database this process serves, so it is `graticula restore` against a stopped server."* The owner compared Graticula
with ArcGIS Enterprise 12.2 (`docs/research/enterprise-12-2-gap.md`, band 2), where disaster recovery is an
Administrator API — backup stores, backups, restore — and chose to have both a **validate** (is this backup whole, and
does it restore?) and a **restore through the API**.

What a restore through the API has to solve that the command line did not:

- **The server is running.** Every node of an [ADR-012](ADR-012-clustering.md) site holds pools, a session that holds
  its name and sweep locks, and a `LISTEN` connection, all to the database being replaced; PostgreSQL will not rename a
  database anybody is connected to.
- **The job's own record cannot be in the database it replaces.** [ADR-011](ADR-011-job-system.md)'s jobs are rows in
  the platform store; a row written before the switch is not in the database after it.
- **The console that started it has to be able to watch it**, while every node is refusing requests.
- **ADR-319's failover (the platform store's standby, written beside this one) switches the host the server connects to**, written at the same time
  by another agent. If restore changed the connection string too, the two could collide.

**Correction to the gap document's context, as the brief asked** (the gap document is the lead's to edit): its row
*"HA and multi-machine: Phases 0–1"* and its item *"HA phase 2"* are stale. ADR-012's server tier — phases 0 to 4, two
nodes, shared server directories, a balancer, rolling upgrade — is built and its conditions discharged. What remains
is the platform store's own availability, which is ADR-319's, not this one's.

## 2. Alternatives considered

### Alternative A — maintenance, restore into a new database, swap names at the database level (chosen)

**Argument for.** The connection string never changes, so nothing that builds or stores it — and nothing ADR-319 does
to it — is touched. The old database is kept whole under a name that says what it is. Every step before the second
rename can be undone by dropping a database and lifting the maintenance.

**Argument against.** `ALTER DATABASE … RENAME` needs nobody connected, so every node's every connection has to be
closed at once, and a node that reconnects in the gap makes the rename fail.

### Alternative B — restore into a new database and point the server at it

**Argument for.** No rename; the old database is untouched by construction.

**Argument against.** The connection string is configuration on every node — an environment variable in a container —
so every node would need a new one and a restart, and it is exactly the value ADR-319 changes. A deployment's
database name would drift with every restore.

### Alternative C — keep restore on the command line (ADR-193 §5.5)

**Argument for.** It works, and stopping the server is the simplest guarantee that nothing writes during a restore.

**Argument against.** The owner reopened it. On a site it means stopping every node by hand; through a console it is
impossible.

## 3. Counterarguments to the preferred option

- **A restore through the API is a button that replaces everything.** ADR-193 §3 said so. The confirmation names what
  is replaced and where the old one is kept; only the system administrator reaches it; the old database is kept, not
  dropped.
- **Maintenance first means the site is down for the whole `pg_restore`**, minutes for gigabytes (deployment.md §2.1
  measured three minutes for a 7 GB dump), when restoring into the new database while the old one still served would
  be down only for the swap. But anything written during those minutes would be discarded silently by the swap; Esri's
  own documentation says the organization *"will be unavailable until the process is completed"*. Honest downtime
  over lost edits. **Confirmed by the owner, 2026-11-23** — *"Baştan sona kapalı"* (down from start to end), asked
  against the alternative of closing only for the swap and losing the edits made meanwhile.
- **The status is a file**, not a job row: one more place state lives. It has to be, for the reason in §1.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| ArcGIS Enterprise restores a backup through the Administrator API, `…/admin/system/disasterrecovery/stores/<store>/backups/<backup>/restore`, and *"the organization being unavailable until the process is completed"*; existing content *"is replaced with the data contained in the backup"* | Esri's REST reference, read 2026-11-23 | https://developers.arcgis.com/rest/enterprise-administration/enterprise/restore-backup/ |
| ArcGIS's `validate` is a backup **store**'s, and checks that the store *"is able to access the object store"* and is *"ready for backup operations"*, answering `isReady` | Esri's REST reference, read 2026-11-23 | https://developers.arcgis.com/rest/enterprise-administration/enterprise/validate-backup-store |
| A restore is a job whose progress the Backups page shows, ending `success`, `success_with_warnings` or `failure`; the organization is inaccessible until it completes | ArcGIS Enterprise on Kubernetes 12.0, *Restore a backup*, read 2026-11-23 | https://enterprise-k8s.arcgis.com/en/12.0/administer/restore-a-backup.htm |
| Create backup, the disaster-recovery resources | Esri's REST reference — listed by search, not read | https://developers.arcgis.com/rest/enterprise-administration/enterprise/create-backup/ |
| PostgreSQL refuses `ALTER DATABASE … RENAME` while anyone is connected, and `ALLOW_CONNECTIONS false` keeps everyone out, superusers included | PostgreSQL 16 documentation, `ALTER DATABASE` and `pg_database.datallowconn` | https://www.postgresql.org/docs/16/sql-alterdatabase.html |
| The command-line restore's sequence (new database, check, terminate, rename, keep) works on a copy of the showcase | ADR-193 condition 1, 2026-10-17 and 2026-10-27 | [ADR-193](ADR-193-backup-and-restore-are-the-server-s-own-commands.md) |

## 5. Decision

1. **Routes**, under `/admin/backups` — so [ADR-204](ADR-204-one-deployment-holds-several-organisations.md)'s
   `TenantRouting.SystemOnly` already keeps them to the system administrator — and behind `AdminManageServer`:
   - `POST /admin/backups/{name}/validate` → 202 and an operation.
   - `POST /admin/backups/{name}/restore` with `{"confirm": "<name>"}` → 202 and an operation. Without the backup's own
     name in `confirm` it is refused, 400, so a stray POST restores nothing. *(`INFERRED`: Esri asks for the backup's
     passcode; this server's backups have none, and the key check is the equivalent refusal.)*
   - `GET /admin/backups/operations/{id}` → the operation; `GET /admin/backups` gains `operation`, the latest.
2. **An operation is an asynchronous job with a status** — `running`, `succeeded`, `failed` — its kind, its backup,
   its steps each with a time, a current step and an error. **Its record is a file**, `.operations/<id>.json` under the
   backup location, written at each step, not a row of ADR-011's jobs: a restore replaces the database such a row
   would be in, and the backup location is the site's server directories (ADR-012 phase 1) by default, so every node
   reads it. One operation at a time, and not while a backup is being taken. *(`INFERRED`.)*
3. **Validate** checks a **backup**, which is what an operator needs before a restore and what this server has (ArcGIS's
   validates a store's reachability; ours has no stores — *`INFERRED`*):
   1. the manifest, every part's SHA-256, the key's fingerprint against this server's, and that this build reads its
      schema — the command-line restore's refusals, in the same words;
   2. the dump restored into a scratch database `<db>_validate_<time>` on the same server, through the same steps a
      restore takes;
   3. the scratch database's schema stamp against the manifest, its hosted tables present, its foreign keys, policies
      and triggers counted against the dump's table of contents (ADR-193's checks), and its services, layers and
      portal items counted and reported;
   4. the scratch database dropped, whatever happened.

   It never connects to the live database: the scratch database is made and dropped through the maintenance connection
   to `postgres`.
4. **Restore** is an operation of these steps, and **a failure at any of them leaves the old database serving**:
   1. **Refusals** — validate's step 1 — before anything changes.
   2. **Maintenance.** Every node's configured state is recorded and set to `maintenance` through ADR-012's own
      mechanism (`site_node`, announced), a marker `site.restore` is written to `server_setting`, and a `restore`
      announcement goes to the site. Every node then answers **503 `restoring`** to everything except `/healthz/live`
      and the operation's own status, which answers without a session — the restored database holds none — and is
      protected by its 128-bit id alone (**confirmed by the owner, 2026-11-23**: *"Kalsın"*, keep it) — the readiness reason is a named state beside ADR-318's, so a balancer stops
      sending it requests. *(`INFERRED`: liveness stays 200 during a restore, unlike a refusal, because a restart of
      the node running the restore would kill it half way.)*
   3. **Drain.** This node's requests in flight finish, and so do the queries other nodes have running on the
      database (`pg_stat_activity`, `active`), each for at most thirty seconds.
   4. **Into a new database**, `<db>_restore_<time>`, exactly as the command line does, and checked as validate checks.
      When the backup's schema is older than this build, the new database is **migrated before the switch**, so no
      node ever serves an old schema. The recorded node states are written into it, and any marker removed from it.
   5. **The state files** — imagery, scene and map-area package files, the serving certificate — put in place as the
      command line does, the replaced ones kept beside them as `…before_restore_<time>`. *ADR-193 §5.3's gate is the
      maintenance*: no node writes a file while every node refuses requests.
   6. **The switch, at the database level** — the brief's suggestion, decided: connections to the current database
      disallowed (`ALLOW_CONNECTIONS false`), the remaining ones ended, the current database renamed
      `<db>_before_restore_<time>`, the new one renamed to `<db>`, the old one's connections allowed again. **The
      connection string does not change**, so nothing ADR-319 does to it can collide with this. If the second rename
      fails the first is undone; if the switch fails, step 5's files are moved back.
   7. **The nodes come back.** This node clears its pools, drops its site session (which takes its name back on the
      next use), lifts its gate and takes up its recorded state; the others lift theirs when they find no
      `site.restore` marker in the database they can reach, checked every three seconds, or at once on the
      announcement.
   8. **The previous database is kept** as `<db>_before_restore_<time>`, and the status says its name. Dropping it is
      the administrator's.

   A failure before step 6 drops the new database, moves any step-5 files back, removes the marker and restores the
   recorded states in the old database, and lifts every gate: the old database serves as before.
5. **Studio's System › Backups** gains *Validate* and *Restore* on each backup. Restore asks first, in a dialog that
   names what is replaced (the database by name, the imagery and the serving certificate), that every node goes into
   maintenance and refuses requests until it ends, and the name the current database will be kept under; the backup's
   name is typed to confirm. An operation's progress is said in an `aria-live` status, polled from the operation.
6. **`graticula restore <backup>` keeps working** and shares the code of steps 1, 4 and 6.
7. **A test can make a step fail**: `GRATICULA_TEST_RESTORE_FAIL_AT=<step>` in the server's environment — an
   environment variable rather than a `Graticula:*` setting, so it is never a site setting nodes compare
   (ADR-012 item 11) and never in a configuration file by accident.

## 6. Consequences

**Positive.** A backup can be proved restorable before it is needed. A deployment is restored from the console, on
one node or a site, without stopping anything by hand, and the way back is a database with a name.

**Negative.** The whole site is unavailable from the first step to the last, which for a large dump is minutes. A
restore needs the same privileges the command line does — creating and renaming databases — so a serving role that
may not needs `Graticula:MaintenanceStore` (ADR-193's amendment). The kept database doubles the space the store takes
until somebody drops it. A node whose backup location is not shared with the executing node does not see the
operation's file — it still lifts its gate from the database marker.

**State.** `.operations/<id>.json` under the backup location; `site.restore` in `server_setting` for the length of a
restore; the kept database.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ADR-193's: the login may create and rename databases (or a maintenance connection may) | `INFERRED` |
| — | No other application connects to the platform store's database (it is renamed) | `INFERRED`, ADR-193's |
| — | The backup location is the site's shared server directories on a site | `INFERRED` — the default; an operator may set it elsewhere |

## 8. Dependencies

**Depends on:** [ADR-193](ADR-193-backup-and-restore-are-the-server-s-own-commands.md), [ADR-012](ADR-012-clustering.md)
(maintenance, announcements), [ADR-204](ADR-204-one-deployment-holds-several-organisations.md) (system only),
[ADR-318](ADR-318-a-refusing-server-stays-up-and-says-why.md) (the named states beside readiness).

**Depended on by:** —

## 9. Revisit triggers

- ADR-319 or anything else makes the platform store's database name change from outside the server.
- A restore whose `pg_restore` takes longer than an operator's window — then restoring before the maintenance and
  replaying nothing becomes worth arguing.
- Backup stores (object storage) are built, when validate gains ArcGIS's meaning too.

## 10. Dissent

None recorded.

## 11. Conditions

1. **Validate on a good backup and on a corrupted one** — the good one succeeds with its counts and leaves no scratch
   database; the corrupted one fails naming the part whose checksum differs, and nothing is created.
   **DISCHARGED 2026-11-23** — `ABackupIsValidatedAndRestoredThroughTheApiTests.A_good_backup_validates_and_a_corrupted_one_is_refused_by_name`,
   a real server over a database of the test's own: a backup taken through `POST /admin/backups` validated `succeeded`
   with its counts and no `_validate_` database left; one byte of its `database.dump` flipped, the same validation
   failed with *'database.dump' is missing or does not match its checksum* and no database was made.
2. **A restore round trip on a real server process**: a backup taken, a marker table created after it, a restore
   through the API; the marker table is gone, the server answers ready, and the previous database is kept under the
   name the status gives.
   **DISCHARGED 2026-11-23** — `A_restore_through_the_api_brings_the_backup_back_and_keeps_the_old_database`: a POST
   without `confirm` answered 400; with it the operation went `checks → maintenance → drain → restore → files → switch →
   return` and `succeeded`, `/healthz/ready` was seen answering 503 `restoring` while it ran, the table created after
   the backup was gone, the kept database existed under the name the status gave, no `_restore_` database was left, and
   the server answered ready and served `/rest/services`.
3. **A failure injected in the switch** — between the two renames — leaves the old database serving: the marker table
   is still there, the server answers ready, no database is left half named.
   **DISCHARGED 2026-11-23** — `A_failure_in_the_switch_leaves_the_old_database_serving`, with
   `GRATICULA_TEST_RESTORE_FAIL_AT=switch` — the first rename done, the second not: the operation `failed` with
   `failedAt: switch`, the table created after the backup was still there, neither a `_restore_` nor a
   `_before_restore_` database was left, and the server answered ready and served. **What the runs found on the way**:
   the status written while the run added to it threw *collection was modified* into a 500, and a status being read held
   the record's file so that replacing it failed on Windows and failed the restore at `files` for nothing — both seen in
   test runs, both repaired (a snapshot under the operation's lock; a record that is retried and never fails its
   operation), and the class then passed 14 of 14 twice with its neighbours.
4. **The console**: Validate and Restore on System › Backups, the confirmation naming what is replaced, progress in an
   `aria-live` status, focus kept after a re-render — a test of the screen, and a ux-designer review.
   **DISCHARGED 2026-11-23, on the ux-designer's third render** (*ship*). The first render rejected it: a real restore
   never said it had finished, because the restored database holds no session and every status read answered 401
   while the page kept polling; the row was crushed at 390 px; the dialog had no width and buried what is lost. Each
   was repaired and measured — a 401 now ends the polling, shows the sign-in and then the result (two real restores),
   the dialog leads with what is lost and opens at its top on a 360×640 screen, a finished operation's steps fold
   under its outcome — and the third render passed both widths and themes with axe and the kit's gates clean.
   `BackupsScreenTests` 5/5, `A_restore_that_ends_the_session_shows_the_sign_in_and_then_its_result` among them.
   *Before the review:* ~~**PARTLY DISCHARGED 2026-11-23**~~ — `BackupsScreenTests.A_backup_is_validated_and_restored_from_its_row` against an
   answered API: Validate followed in the polite live region `bkrProgress` to *98 services*; Restore asks in `bkrDialog`,
   naming the database, `_before_restore_` and *the imagery and the serving certificate*, sends nothing until the name is
   typed, and both times focus is back on the pressed button after the list is drawn again (which it was not before:
   `drawBackups` replaced the rows under the reader). 4 of 4 of the class passed. ~~The ux-designer review is owed~~ —
   the screen is `/system/backups`.
5. **A site of two nodes** restored through one of them: both answer 503 `restoring` during it and both come back.
   **DISCHARGED 2026-11-23** — `A_restore_through_one_node_of_a_site_closes_and_brings_back_both`: two real nodes over a
   database of the test's own, the restore through `dr-a`; `dr-b` was seen answering 503 `restoring` while it ran, and
   both answered ready again on the restored database, which no longer held the table made after the backup. A node that
   misses the announcement is [D-490](../architecture-debt.md).
6. **On the fixture**: a round trip measured on a seeded fixture, with the conformance suite after it.
   **PARTLY DISCHARGED 2026-11-23** — the local fixture (port 18632, database `gisg2d`, seeded as CI seeds it): a backup
   through the API in 2.2 s; *Validate* succeeded in 3.2 s (`checks → restore → count → drop`, 8 services, 9 layers,
   8 items, schema 146, the scratch database gone); a table created after the backup; *Restore* succeeded in 3.1 s
   (`checks → maintenance → drain → restore → files → switch → return`, nothing still running at the drain),
   `/healthz/ready` seen 503 `restoring` during it and 200 after, the table gone, the 9 layers there, and
   `gisg2d_before_restore_20261123164505` kept. **The conformance suite after it ran 986 of 1030**; the first run, 768 of
   1030, was the shared PostgreSQL's 100 connections running out under other agents' suites (`53300`, 236 lines of the
   server's log), not the restore. Of the 44, 22 cannot find the repository root (this build's output is outside the
   tree), 7 are user toolboxes that need Linux's Landlock, and the rest — a wrong password reported *Usable* by a
   cluster that trusts local logins, the request-log tests reading another fixture's log — were read and none touches a
   restored row; **they were not compared against a run on ae8e76c7**, which is why this is partly.
