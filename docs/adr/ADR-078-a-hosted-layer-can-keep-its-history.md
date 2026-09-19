# ADR-078 — A hosted layer can keep its history, and the database keeps it

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` that the database keeps it · `MEDIUM` on the write cost, which is measured once on one machine (§4) |
| **Decided** | 2026-09-19, by owner decision, after a comparison with NextGIS Web ([research/nextgis-web-comparison.md](../research/nextgis-web-comparison.md)), which keeps every version of a vector feature. The comparison named two things Graticula lacks that a user would notice, and the owner answered: *"2. Kapsama alalım"* — take them into scope. **That feature history is in v1 is the owner's.** **Opt-in per layer, the ArcGIS archiving shape, restore as a new edit, and hosted layers only are `INFERRED`** and listed in §11 — *(Confirmed by the owner 2026-09-19, all of them, after each was put in plain words: *"onaylıyorum"* — Q-155.)* |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Until today, the server could not say what a feature looked like yesterday. `historicMoment` was on
the query parameter list as *accepted and ignored — "there is no history"*
(`FeatureServerQueryParameters`), and the inventory scan told a migrating administrator that an archived
ArcGIS layer would arrive here with its history gone (`InventoryScan`: *"historic moments are not served
here"*). `applyEdits` writes one audit row per batch with counts and nothing else, on purpose.

NextGIS Web keeps each version of a feature, with who changed it and when, and can roll one back. An
ArcGIS Enterprise administrator has the same thing under a different name: **archiving** — every version
of a row kept with the moment it became true and the moment it stopped, and a `historicMoment` on the
query that answers the layer *as it was*. Both products carry it, so a user moving from either would miss it.

**The question this ADR has to answer first is not *how* but *who records it*.**
[ADR-005](ADR-005-api-architecture.md) §3.8 already answered it in the abstract: *anyone with database
credentials — QGIS, a script, a DBA — can write rows directly … change history built on our own record of
events will miss direct writes.* It listed three responses, and the second is the one that is right for
history: **push the bookkeeping into the database, so any writer updates it.** It also named the price:
*it requires DDL rights*. [ADR-002](ADR-002-primary-data-architecture.md) §4.2 is the rule on DDL:
*this server writes rows into a database it does not own, and never schema.* So the answer for where it
can exist was already written down before the question was asked: **in the datastore, on hosted layers.**

## 2. Alternatives considered

### Alternative A — The writer records each edit in a platform table

**For.** No DDL anywhere; works for registered layers too; the writer knows the Graticula user exactly.

**Against.** It is the design ADR-005 §3.8 rules out by name. QGIS on the owner's showcase edits the
datastore directly every day, and every one of those edits would be missing from a history that claims to
be the history. A record that is silently incomplete is worse than no record, because someone restores
from it.

### Alternative B — A trigger on the hosted table writes every version into a companion table *(chosen)*

**For.** Every writer is recorded — this server, QGIS, `psql`. It is ArcGIS archiving's own shape (a
version per row state with a from and a to), so `historicMoment` falls out of it rather than being
emulated. PostgreSQL does the work inside the writer's own transaction, so a rolled-back edit leaves no
version behind.

**Against.** Every write to an archived layer pays a second write. It is DDL, so it cannot be offered on a
registered layer. The trigger cannot know the Graticula user on its own; it has to be told (§5.3).

### Alternative C — PostgreSQL's own machinery: logical decoding or `temporal_tables`

**For.** Nothing in the write path at all (logical decoding), or a maintained extension.

**Against.** Logical decoding needs a replication slot and a consumer that is always up; a slot left behind
fills the disk, which is exactly the 2 AM failure this project designs against. `temporal_tables` is an
extension the datastore image does not carry and a registered database would never be asked to install.
Both are more moving parts than a forty-line trigger function.

### Alternative D — Branch versioning (`gdbVersion`)

**For.** It is what ArcGIS offers for editing in isolation.

**Against.** It answers a different question — *edit in private, then reconcile* — and it is an order of
magnitude larger. Nobody asked for it. `gdbVersion` stays on the ignored list with *"there is no version
tree"*, which remains true.

## 3. Counterarguments to the preferred option

- **Opt-in means the layer somebody needed history on is the one nobody turned it on for.** True, and the
  alternative — on for every hosted layer — adds a second write to every write, about 40 % on an update (§4a), on a server whose owner bulk-loads Türkiye
  datasets. The layer page shows the switch beside the fields; §11 lists the choice as inferred.
- **A trigger is invisible.** Somebody reading the table in `psql` does not see that writes go twice. The
  table is named `<table>__history` beside `<table>__attach`, which is the naming this server already uses
  for its companions, and dropping the layer drops it.
- **The editor name is a hint the writer passes, not a fact the database knows.** `set_config` inside the
  transaction is honoured only for writes through this server; a direct write records the database role.
  That is the truth — the database knows the role, not the person — and the history says which it is.
- **`jsonb` loses column types.** It does, which is why a historic read goes back through
  `jsonb_populate_record` against the table's own row type: the type comes from the table, not from the
  JSON, and a column added since reads as null, which is what it was.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Direct writes to the datastore happen | ADR-005 §3.8; the owner edits the showcase's data in QGIS | ADR-005 |
| A write through the server and one straight to the database are both kept, with the account for the first and the database role for the second | `FeatureHistoryTests.An_edit_through_the_server_and_one_straight_to_the_database_are_both_kept` — and it **fails** with the writer's `set_config` removed (checked by mutation, 2026-09-19) | `tests/Graticula.Platform.Postgres.Tests` |
| A rolled-back batch leaves no version | `…A_batch_that_is_rolled_back_leaves_no_version` | same |
| A historic moment reads the layer as it was, through features, count and ids | `…A_historic_moment_reads_the_layer_as_it_was` | same |
| Two updates in one transaction are one version | `…Two_updates_in_one_batch_are_one_version` | same |
| A restore writes the version back, the restorer is the tracked editor, and the restore is itself a version; a deleted feature comes back under its own id | `…A_restored_feature_is_the_version…`, `…A_deleted_feature_comes_back_under_its_own_object_id` | same |
| A truncate ends every feature; turning history off removes table, triggers and function; a registered table is refused | `…Emptying_the_table_ends_every_feature`, `…The_description_says_whether…`, `…A_table_outside_the_hosted_schema_is_refused` | same |
| `historicMoment` is honoured on an archived layer and refused on any other, with the reason | `HistoricMomentQueryTests` | `tests/Graticula.Host.Tests` |
| End to end over HTTPS: enable, `isDataArchived` true, `applyEdits` an update and a delete, `historicMoment` returns the three features as they were and `returnCountOnly` says 3, the History API lists both changes by `ci`, both restores put the layer back as it began | run by hand against a local fixture, 2026-09-19 | this ADR |
| Write cost | measured, §4a | [benchmarks/feature-history](../../benchmarks/feature-history/RESULTS.md) |

### 4a. What the trigger costs

[benchmarks/feature-history/RESULTS.md](../../benchmarks/feature-history/RESULTS.md), 500 features in
one `applyEdits` against a 600-polygon hosted layer, end to end, on the development machine:

| | history off | history on |
|---|---|---|
| 500 updates | 286 / 304 ms | 416 / 429 ms — **about 1.4×** |
| 500 adds, then 500 deletes | within run-to-run noise of each other | |

**The first version of the trigger doubled an update — 274 against 561 ms — and was replaced before it
was committed.** It was one function per schema that built its statements with `format` and `execute`,
which PL/pgSQL plans again on every row; one function per table with the names written in keeps its plans.
Condition 1 holds this ADR to the number being re-measured on the datastore image the showcase runs.

## 5. Decision

1. **History is a property of a hosted layer, switched on and off by its owner.** `POST
   /admin/layers/{name}/history` with `enabled=true|false`. On a registered layer the answer is a 409 that
   names ADR-002 §4.2. Turning it off drops the trigger **and the history**, after a confirmation that says
   so; a history kept after the trigger stops is a history with a hole in it.
2. **The database keeps it.** Turning it on creates, in the hosted schema, `<table>__history` and a row
   trigger on `<table>`:
   - one row per *version* of a feature: the object id, `gdb_from_date`, `gdb_to_date` (null while it is
     current), the editor who made it and the editor who ended it, the attributes as `jsonb`, and the
     geometry in its own typed column with a spatial index;
   - insert opens a version; update closes the current one and opens another; delete closes it; a
     truncate closes every open one. An update that changes nothing records nothing. Two updates to one
     feature inside one transaction leave one version, because `now()` is the transaction's moment and a
     version that began and ended at the same instant was never true;
   - the current rows are copied in when history is switched on, under a lock that stops a write slipping
     between the copy and the trigger.
3. **Who.** The writer sets `graticula.editor` with `set_config(…, true)` at the start of every edit
   transaction, so it lasts exactly as long as the transaction. The trigger reads it, and falls back to
   `session_user` — a direct write is recorded as the database role that made it, and the history page says
   *database role* beside it rather than pretending it is a person.
4. **Three faces read it.**
   - **ArcGIS `historicMoment`** on a layer's `query` (features, count, ids, extent, statistics): the layer
     as it was at that instant, epoch milliseconds, through the same filters, ordering and paging as a
     current read. The layer document says `isDataArchived: true` and
     `advancedQueryCapabilities.supportsQueryWithHistoricMoment: true`. On a layer without history
     `historicMoment` is refused with a 400 rather than ignored, because ignoring it answers *now* to a
     question about *then*, which is the silent wrong answer ADR-008 §2 forbids — that the parameter sat on
     the ignored list was a defect of its own, and this is its repair.
   - **The admin API**: `GET /admin/layers/{name}/history` — the layer's changes, newest first, paged; and
     `GET /admin/layers/{name}/history/{objectId}` — one feature's versions.
   - **Studio**: a *History* page on the layer — who changed what and when, a feature's versions side by
     side with what changed between them, and a restore button.
5. **Restore is an edit.** Restoring a feature writes the version's whole row back to the table — an
   update if the feature exists, an insert under its old object id if it was deleted — in a transaction
   that names the restorer, so the trigger records the restore as a new version and it can be undone by
   restoring the version before it. Nothing in history is ever rewritten. It asks what any edit asks
   (ADR-075: the owner, an administrator, or a group shared for editing), and a tracked layer's editor
   columns carry the restorer. **It does not go through the `applyEdits` writer**, and that is chosen:
   the writer takes the attributes a client sends, and a restore is every column exactly as it was —
   including ones a client cannot see or write, like the GlobalID. The cost is that a domain narrowed
   since the version was written does not refuse the restore; condition 5.
6. **What is not kept.** Attachments (their companion table is not archived; a deleted feature restored
   comes back without them, and the restore says so). Schema: a dropped column's values stay in the
   versions' JSON and are not restored into a column that is gone.

## 6. Consequences

**Positive.** A layer's past is answerable by an ArcGIS client in its own words, and by the owner in
Studio. The history includes the edits made in QGIS, which is the case that would otherwise be missing.

**Negative.**
- Every write to an archived layer writes twice; §4a says how much.
- History grows without bound. There is no pruning, and ArcGIS has none by default either; condition 3.
- The companion table is DDL the server now runs on a live table under a lock; switching it on for a
  multi-million-row layer copies every row once and blocks writers for that long.

**State.** Nothing in the catalogue. The history lives in the datastore beside the layer — `<table>__history`, its triggers and its function `<table>__history_fn` — shared by every node because it is in the database, and read from there on every describe rather than remembered: whether a layer keeps its history is the trigger's existence. The only runtime copy is the describe cache's `Archived`, bounded by that cache's lifetime and forgotten when history is switched.

**Ports created.** None. `PostGisFeatureHistory` is Graticula code over PostgreSQL; there is no Tier 2
dependency.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | `now()` in a trigger is the transaction's start, so one batch is one moment | PostgreSQL documented behaviour |
| — | `jsonb_populate_record` against the table's row type recovers every column type, geometry included, from the text form the trigger stored | tested (`FeatureHistoryTests`) |

## 8. Dependencies

**Depends on:** ADR-002 §4.2 (no DDL outside the datastore), ADR-005 §3.8 (bookkeeping the database
keeps), ADR-013 (the feature data model), ADR-064 (the writer's editor name), ADR-008 §2 (no silent
degradation).

**Depended on by:** —

## 9. Conditions

1. **The write cost is re-measured on the datastore image** — linux-arm64, the showcase's — and written into
   §4a beside the first number.
2. **An ArcGIS client is shown reading a historic moment** — the SDK's `FeatureLayer.historicMoment` or
   Pro's time slider on an archived layer — against a running server. Until then the claim is that the
   response has the right shape, not that a client uses it.
3. **History has a retention answer before a layer has one in production** — a *keep for N days* on the
   layer, or a recorded decision that there is none.
4. **Attachments.** Archived or recorded as never; a restore that silently drops a photo is the case to
   design against. Today the restore does not drop them silently — it says so — but it does drop them.
5. **A restore meets today's domains.** A version written before a column's list of values was narrowed
   is restored with the old value. Either the restore checks the layer's domains as `applyEdits` does, or
   this is recorded as the rule.
6. **A historic read's spatial filter is measured on a large layer.** It cannot use the history's spatial
   index (the geometry it tests is rebuilt from the version's text), and nobody has measured what that
   costs on a layer of a million features.

## 10. Revisit triggers

- A registered source is granted DDL rights and its owner asks for history on it — ADR-002 §4.2 is the rule
  that would have to change first.
- The write cost on the showcase exceeds twice the unarchived write for a 1,000-feature `applyEdits`.
- Somebody asks for branch versioning (`gdbVersion`); that is a new ADR, not an extension of this one.

## 10a. Dissent

None recorded. The case for *on by default* is written in §3 and was not adopted.

## 11. Inferred, and confirmed

The owner decided that feature history is in scope. These were chosen here and listed so that they could be
overturned; *(Confirmed by the owner 2026-09-19, all of them, after each was put in plain words: *"onaylıyorum"* — Q-155.)* They are the owner's now, and overturning one is a new decision
rather than a correction:

- **Opt-in per layer**, not on for every hosted layer.
- **ArcGIS archiving's shape** (`gdb_from_date`/`gdb_to_date`, `historicMoment`) rather than NextGIS Web's
  numbered versions.
- **Hosted layers only.** Derived from ADR-002 §4.2 rather than chosen, but it is a limit the owner has not
  seen stated.
- **Turning history off deletes it.**
- **Restore writes a new version** rather than rewinding.
