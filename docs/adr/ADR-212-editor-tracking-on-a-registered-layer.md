# ADR-212 — Editor tracking and attachments on every registered database's layers

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-10-21, by owner decision: *"tüm db lerde (duck'lar hariç), attachments olmalı. editor tracking de olmalı. yani en son değişim, kim değiştirdi vs bilgileri."* — every database but the DuckDB ones keeps attachments and records who created and last changed a feature, and when. |
| **Reverses** | [ADR-064](ADR-064-editor-tracking-and-what-features-edit-means.md)'s rule, restated in ADR-058 §5h, that this server adds no column to a registered table — for the four editor-tracking columns, on an administrator's word |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

Measured on `main` at v1.0.449 before anything was changed:

- **Attachments** were kept beside a registered table (ADR-207) on PostGIS, Oracle, MySQL and MariaDB, or in the
  datastore for any of them; SQL Server's were refused (ADR-210 condition 5).
- **Editor tracking**: every writer — PostGIS, Oracle, MySQL, MariaDB, SQL Server, DuckDB — signs the columns a layer
  gives the roles *creator*, *created*, *editor* and *edited* (ADR-064), and the tests of each prove it. But on a
  **hosted** layer one call adds Portal's four columns and gives them their roles, while on a **registered** layer the
  roles could only be given to columns a DBA had already added: *"the table is the customer's and this server issues no
  DDL"*. So "who changed it and when" on a customer's own database needed someone with that database's DDL rights.
- **The console** shows *Keep history* and *Who creates and edits* for hosted layers only, and has no control for a
  registered layer's attachments at all; those are API calls.

ADR-207 already crossed the line ADR-064 drew: on an administrator's word this server creates attachment tables and
history triggers in the customer's database. The four tracking columns are a smaller step on the same path.

## 2. Alternatives considered

- **Keep ADR-064's rule**: a DBA adds the columns, an administrator gives the roles on the Fields page. Works; is not
  what was asked.
- **One call for registered layers too, through each engine** *(chosen)*.

## 3. Counterarguments

- **It alters a customer's table.** Only on an administrator's explicit call, only by adding four nullable columns,
  never by dropping them: stopping keeps the columns and what they hold, as Portal does and as the hosted call does.

## 4. Evidence

| Claim | Evidence |
|---|---|
| SQL Server keeps attachments beside its table | `SqlServerAttachmentStore`: chunked `varbinary(max)` rows, cascading foreign keys, quota, a missing feature refused; provider tests 3 new, suite 18 of 18 against LocalDB |
| Each engine adds the four columns once | `AddColumnsAsync` tests: SQL Server (LocalDB), MySQL (MariaDB 11.8), Oracle (26ai Free) — added once, typed as a name and a moment |
| The whole path, on each engine | On the local server, for a registered **PostGIS, MariaDB, Oracle and SQL Server** layer each: attachments switched on beside the table, added through `addAttachment`, listed, read back, deleted; editor tracking switched on with **one call** — its columns added in the customer's database — the layer document carrying `editFieldsInfo`, an `applyEdits` add signed with the account and dated, tracking switched off with the columns kept, attachments off: **14 of 14 on all four** (`D:/temp/tracking-attach-e2e.py`, 2026-10-21) |

## 5. Decision

1. **`POST /admin/layers/{layer}/editor-tracking`** (and the hosted path as before) adds `created_user`,
   `created_date`, `last_edited_user` and `last_edited_date` where they are not there, and gives them their roles. On a
   registered layer the columns are added in its own database by its own engine — `varchar2(255)`/`timestamp` on
   Oracle, `varchar(255)`/`datetime(6)` on MySQL and MariaDB, `nvarchar(255)`/`datetime2` on SQL Server,
   `text`/`timestamptz` on PostGIS — and the engine's cached columns are forgotten so the next read sees them. A column of
   one of those names that is already there and of the wrong type is refused naming it; a database that refuses the
   `ALTER` is said with what right it needs.
2. **`DELETE …/editor-tracking`** takes the roles away and keeps the columns and what they hold.
2a. **An administrator's word, not an owner's** (`admin:manageAllContent`): adding the columns to a registered table, and
   keeping attachments *in its own database*, change the customer's database with the registered source's credentials,
   which a layer's owner who did not register the source never held — a review of the branch found the owner was
   enough. Keeping attachments in Graticula's datastore stays the owner's, as ADR-207 has it.
2b. **An `ALTER` waits five seconds for the table, then is refused** (PostGIS `lock_timeout`, MySQL `lock_wait_timeout`,
   SQL Server `LOCK_TIMEOUT`, Oracle `ddl_lock_timeout`), as the hosted path's does (ADR-058 §5g): otherwise one long
   reader holds the `ALTER`, and every query of the layer queues behind it. Oracle's column check is case-blind, so a
   `CREATED_USER` already there is reused rather than given a quoted twin.
2c. **A column added from outside needs no restart of any service** — amended 2026-10-21, on the owner's question
   whether one should be asked for, as ArcGIS Server asks. A table's shape is kept by table, not by service, for 30
   seconds, so every layer and view over it reads the new columns within that; `POST /admin/layers/{name}/refresh` does
   it at once. **Refresh did not, on Oracle, MySQL and MariaDB, and SQL Server**: their engines keep the columns a
   minute below the shape, and Refresh forgot only the shape — so it answered *refreshed* and served the old columns
   for up to a minute more. Measured on SQL Server: a column added outside was missing 36 seconds later with no refresh,
   and listed straight after one once Refresh forgets the engine's columns too. Refresh now also empties the tiles of
   the layer's views, as a schema change does. **What does need a reload is the client**: ArcGIS Pro and Field Maps keep
   a layer's fields from when they opened it, and the tracking answer says so.
3. **SQL Server keeps attachments beside its table**, as the other engines do.
4. **The DuckDB file and MotherDuck** are not given either, by the owner's word; their writer signs columns that are
   given roles, as every writer does.

## Conditions

1. **The console** shows *Keep history*, *Who creates and edits* and the attachments switch for a registered layer in
   a database as it does for a hosted one, through the ux-designer review every screen gets. **DISCHARGED 2026-10-21** — Settings › Feature layer
   shows *Attachments* (in its own database / in Graticula's datastore / off), *Keep history* and *Who creates and edits*
   for a registered PostGIS, Oracle, MySQL, MariaDB or SQL Server layer, through three rounds of the ux-designer review:
   a confirm before anything is made in the customer's database, the four columns named, a refusal kept under the row
   that was pressed as an alert, one layout from 390 to 1280 px; axe clean, contrast measured, keyboard throughout.
2. **SQL Server's history and relationships** (ADR-210 condition 5's remainder) — **DISCHARGED 2026-10-31**, with ADR-210 condition 5, whose text carries the evidence (`SqlServerHistoryAndRelationshipsTests`, and history and `queryRelatedRecords` through the fixture's FeatureServer). Not asked for here, recorded so the
   gap stays visible.

## 6. Consequences

- On every registered database but DuckDB, "who changed this and when" is one call away.
- **State.** Nothing new in the catalogue — the roles are field overrides, as before. In the customer's database, four
  nullable columns where the call was made.

## 7. Assumptions this decision rests on

- That four nullable columns are an acceptable change to a customer's table when its administrator asks for them.

## 8. Dependencies

- ADR-064 (the roles), ADR-115 (stopping keeps the columns), ADR-207 (attachments and history beside a registered
  table), ADR-210 (SQL Server).

## 9. Revisit triggers

- A customer whose table may not be altered at all — the Fields page's roles on existing columns still serve them.

## 10. Dissent

None recorded.
