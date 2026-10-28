# ADR-193 — Backup and restore are the server's own commands, and they take the database with them

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-17, by owner decision (*"Uygulamaya bir backup restore yapısı lazım. ama o backup restore yapısının db backup'ı ve restore'unu da çağırması lazım"*, then *"yap"* to the proposal below, including its four recommendations) |
| **Supersedes** | [deployment.md](../deployment.md) §2.4's *"no backup mechanism ships, and that is a decision"*; answers [Q-177](../open-questions.md) |
| **Superseded by** | — |

---

> **Amended 2026-10-19 — [ADR-204](ADR-204-one-deployment-holds-several-organisations.md) condition 9.** A server
> that connects as a role row-level security binds — neither a superuser nor `BYPASSRLS` — could not restore: it
> may not create the new database, and the dump's `create extension postgis` needs a superuser, so every geometry
> table failed and the check refused (*8 hosted layer(s) name a table that is not in it*). **`Graticula:MaintenanceStore`**,
> optional, is a connection to the same server as a role that may: when it is set, the database-level steps go
> through it — the new database created owned by the serving role, PostGIS put in first — and `pg_restore` still
> runs as the serving role; the roles file is read through it too, which the serving role may not. Unset, nothing
> changes. Measured in [benchmarks/tenant-floor/ROLES.md](../../benchmarks/tenant-floor/ROLES.md).

## 1. Context

[ADR-016](ADR-016-packaging-deployment-upgrade.md) §4b promised that `migrate` *"takes a backup first"* and §6 that
the admin API *"gains migrate, contract, backup"*. Neither was built; deployment.md §2.4 then decided that no backup
ships, because what a backup verb would do is run `pg_dump`. The ArcGIS gap review of 2026-10-17 found the two
documents contradicting each other, and Q-177 asked the owner whether a backup command should exist. It should.

**What a backup has to carry was measured on the showcase the same day:** the database (7.4 GB — the catalogue,
the hosted tables, the documents, the identity store, all in one), the uploaded imagery under the state directory
(84 MB), and the serving certificate (4 KB). Thumbnails, exports and DuckDB extensions are regenerated or
re-downloaded. The secret key and any GeoParquet folder are outside both, by design (deployment.md §2.5).

## 2. Alternatives considered

### Alternative A — the server's own `backup` and `restore`, which run `pg_dump`/`pg_restore` (chosen)

**Argument for.** One command an operator learns, the same on the appliance and on an organisation's own
PostgreSQL; it knows which files are state, checks the key and the schema version before it overwrites anything,
and restores into a new database first so a failed restore leaves the old one standing. `migrate` can call it.

**Argument against.** It ships PostgreSQL's client tools in the server image and has to keep their major version at
or above the server's.

### Alternative B — document `pg_dump` and a file copy, ship nothing (deployment.md §2.4 until today)

**Argument for.** No code; operators' own tooling.

**Argument against.** Every step the documentation lists is a step somebody skips, and the two that matter most —
the key and the schema version — are exactly what a hand-run restore does not check. The owner decided against it.

### Alternative C — a backup agent inside the datastore container

**Argument for.** `pg_dump` already lives there.

**Argument against.** It only exists on the appliance; a deployment on its own PostgreSQL would have no backup, and
the state files are in the server's volume, not the datastore's.

## 3. Counterarguments to the preferred option

- **A backup the server takes of itself is only as good as the moment it is taken.** The database is one snapshot,
  the files a copy after it; an image uploaded or deleted between the two would leave a row without its file. The
  imagery writers wait on a backup gate while the files are copied (§5.3).
- **Restore cannot run while the server serves the database it replaces.** It is a command for a stopped server,
  not a button (§5.5).

## 4. Evidence

The sizes above (showcase, 2026-10-17). deployment.md §2.1 measured a 7 GB dump at six minutes and its restore at
three, and §2.3 the catalogue/hosted-table mismatch a restore into the wrong cluster produces.

## 5. Decision

1. **`graticula backup [<directory>]` and `graticula restore <archive>`**, verbs of the server binary as `migrate`
   is. A backup is a folder `graticula-backup-<UTC time>/` under `Graticula:BackupPath` (default `backups` under the
   state directory) holding `manifest.json`, `database.dump` (`pg_dump -Fc` of the whole database — never one
   schema, §2.2) and `state/` (imagery and the serving certificate); `roles.sql` (`pg_dumpall --roles-only`) when
   the connection may read roles.
2. **The manifest is written last** and names the product version, the platform schema version, the PostgreSQL and
   PostGIS versions, each part's SHA-256 and the secret key's fingerprint. A folder without a manifest is an
   unfinished backup and restore refuses it.
3. **Consistency: the state files are copied twice, around the dump** — once before, so a file deleted while the
   database is dumped is still there for the row the dump holds, and once after, for a file added before the dump took
   its snapshot. An extra file is harmless and a row without its file is not; this leaves none, with no lock on the
   imagery writers. *(The proposal said a gate on the writers; there are a dozen of them, and two passes need none.)*
4. **The secret key is not in the archive** (owner decision on the recommendation): its fingerprint is, so whoever
   holds a backup does not hold the credentials of every registered database.
5. **Restore runs only from the command line, against a stopped server** (owner decision on the recommendation). It
   checks the manifest and the checksums; refuses when the configured key's fingerprint differs (every registered
   source would fail to open) or when the archive's schema is newer than this binary; restores into a new database,
   checks that the catalogue's hosted tables exist, then renames the current database to
   `<name>_before_restore_<time>` and the new one into its place — the old one is the way back; puts the state files
   in place the same way; and runs `migrate --apply` when the archive's schema is older.
6. **Server › Site gains Backups** (`/admin/backups`): take one as a job, list, download, delete; a schedule, **off
   by default** (owner decision on the recommendation), with a number to keep.
7. **`migrate --apply` takes a backup first**, ADR-016 §4b's promise, unless `--no-backup` is given.
8. **Where:** a folder next to the server (owner decision on the recommendation); object storage is later.

## 6. Consequences

**Positive.** One command restores a deployment whole, and refuses the two restores that would look successful and
be broken: the wrong key and the wrong schema. ADR-016's promise and deployment.md stop contradicting each other.

**Negative.** The server image carries PostgreSQL's client tools; a backup on the same disk as the server is not a
disaster recovery until it is copied elsewhere, which the screen says.

**State.** Backup folders under `Graticula:BackupPath`; no new table — a backup's record is its manifest.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The platform store's connection may run `pg_dump` of its whole database | `INFERRED` — true for the appliance's `gis` owner; an organisation's restricted login may not |
| — | A rename of the database is acceptable to the deployment (no other application connects to it) | `INFERRED` |

## 8. Dependencies

**Depends on:** [ADR-016](ADR-016-packaging-deployment-upgrade.md), [ADR-002](ADR-002-primary-data-architecture.md) §4.7 (the key), [ADR-011](ADR-011-job-system.md) (jobs).

**Depended on by:** —

## 9. Revisit triggers

- A deployment whose platform login cannot read the whole database or rename it.
- A backup that takes longer than the window an operator has, which is the point object storage or incremental
  backups are argued for.

## 10. Dissent

None recorded.

## 11. Conditions

1. **A round trip on a copy of the showcase** *(measured on the local fixture 2026-10-17: a table created after the
   backup was gone after the restore, the nine layers were there, and 35 conformance tests passed on the restored
   database)*: back up, restore into another database on the VPS, and the catalogue,
   a hosted layer's rows, an image service and a sign-in answer the same — measured, not assumed.
   **DISCHARGED 2026-10-17 on dev.graticula.com**, whose `gis_dev` is a copy of the showcase's `gis`: the backup
   through `POST /admin/backups` took 6 min 13 s and wrote 3.08 GB; `restore`, with the server stopped, took 3 min 9 s.
   A marker table created after the backup was gone afterwards, and a sign-in, the 98 services in 6 folders, the row
   counts of five hosted layers, an image service's description and the 67 portal items answered exactly as before.
   **Measured as a superuser, and that was the gap** — 2026-10-27: served as a role row-level security binds, which
   the showcase has been since 2026-10-24, the restore's `--jobs` lost 54 of 70 foreign keys to *query would be affected
   by row-level security policy* and reported success. Repaired the same day: the post-data section goes in one pass,
   and the restore counts foreign keys, policies and triggers against the dump and refuses when short
   ([D-317](../architecture-debt.md)); measured on a fixture served as a bound role, all 86 back, and the conformance suite on the
   restored database 887 of 892 with only the five failures this machine always has.
2. **The two refusals have tests**: a different key and a newer schema each refuse before anything is written.
   **DISCHARGED 2026-10-17** — `BackupRefusalTests`, against a connection string that reaches no server, so a refusal
   that came after a connection would fail differently; a broken checksum and an unfinished folder are refused too.
3. **`migrate --apply` takes a backup first**, with a test that it does and that `--no-backup` skips it.
   **DISCHARGED 2026-10-17** — `MigrateTakesABackupFirstTests`: a real backup that cannot be taken (no server, no
   `pg_dump`) stops the migration with nothing applied and nothing kept, a backup that succeeds comes before the
   migration, and `--no-backup` neither builds the backup service nor takes one. The first test was seen to fail with
   the refusal removed.
4. **The Backups screen** takes, lists, downloads and deletes, and its schedule keeps the number it is told to. *(The
   API is built and tested — `/admin/backups`, `BackupEndpointsTests`; the screen belongs to the console redesign,
   which is Codex's since 2026-10-16.)* **DISCHARGED 2026-10-28**, the console's UI having come back on that day:
   System › *Backups*, the machine's because a backup is every organization's (`TenantRouting.SystemOnly`). It takes
   one and says once, in its status line, when it finished or why it failed; lists them newest first with their size,
   version, the UTC time the schedule speaks in and whether the roles were saved; downloads through the console's
   ask-first download (`check=1`, added to the route); deletes behind a confirmation; and sets the daily schedule.
   `BackupsScreenTests` drives all of it against an answered API, and on the local fixture a real backup was taken,
   checked, downloaded as a tar holding `database.dump`, `roles.sql`, `state/` and the manifest, and deleted. Keeping
   the number it is told to is `Backups.Prune`, which the scheduler now calls, held by `BackupScheduleKeepsTests`:
   five finished, keep two, the newest two stay and a folder with no manifest — one still being written — is not
   touched. A ux-designer review sent the first build back: on the real server `pg_dump` was not on the path, the
   backup failed before the page read the list again, and *The backup started* stayed on screen beside a raw
   operating-system error naming the working directory. Now the run is watched from the press (seen to fail with
   that one line removed), the error says to install the client tools or set `Graticula:PostgresTools`, and the
   list answers `toolsMissing` so the page says it before Take. **Not built, from the same review:** the last
   *scheduled* run's own outcome apart from a manual one's, and a link from the restore line to a runbook.
