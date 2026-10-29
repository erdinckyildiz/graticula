# ADR-227 — grapy holds branch versions and archives in the GeoPackage

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-30, by owner decision on scope (*"Versiyonlama ve arşivleme"*), [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.7. The design below is INFERRED and listed for confirmation in §5.5. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

About sixty names are refused because a GeoPackage has no versions and no archive: RegisterAsVersioned,
CreateVersion, ReconcileVersions, PostVersion, EnableArchiving, `da.ListVersions`, `da.Version`, the Editor's
versioned sessions, and the cursors' historical moments. Esri has two versioning models. *Traditional* versioning
keeps delta tables. *Branch* versioning keeps every row's history in the table itself, with a moment and a branch
id. The branch model's rules are documented in public: one default branch, named branches off it, reconcile by
detecting conflicts, post into default.

The server already keeps a hosted layer's history in the database ([ADR-078](ADR-078-a-hosted-layer-can-keep-its-history.md)).
That is the server's model. This ADR is about the data a *user tool* edits.

## 2. Alternatives considered

### Alternative A — the branch model, in grapy's own columns (chosen)

A versioned class gains `gdb_from_date`, `gdb_branch_id`, `gdb_is_delete` and `gdb_deleted_by`, as the branch model
documents. Cursors opened on a version read the latest row per object id on its branch or default before its
creation moment. **For:** one table, history is free, and archiving is the same columns. **Against:** queries grow
more complex, and a reader that is not grapy sees every historical row.

### Alternative B — traditional versioning with delta tables

**For:** the classic model, with more tools around it. **Against:** compress, state trees and lineage are the
complex parts, and their documented rules are thinner. Rejected. Traditional-only names are refused by name.

### Alternative C — one GeoPackage file per version

**For:** simple. **Against:** reconciling means comparing whole files, and archiving does not fit. Rejected.

## 3. Counterarguments to the preferred option

A non-grapy reader of a versioned GeoPackage sees duplicates. Mitigation: versioning is opt-in (RegisterAsVersioned),
and the default branch's current state can be exported with the documented tools.

## 4. Evidence

Branch versioning's row columns and its reconcile and post rules are published (*branch versioning* concept pages,
ReconcileVersions and PostVersion references). Conflict detection by object and by attribute is defined there.

## 5. Decision

1. ~~RegisterAsVersioned (branch) adds the documented columns and keeps every edit as a new row.~~ **Amended 2026-10-30, when it was built:** the class keeps DEFAULT's current state, and its history lives beside it in `<table>__history` with the branch model's documented columns (`gdb_objectid`, `gdb_from_date`, `gdb_branch_id`, `gdb_is_delete`, `gdb_deleted_by`). Triggers append a row for every insert, update and delete, whoever writes it. Edits on a named version go to the history table under its branch. **Why:** with history in the table, every reader that is not a grapy cursor, grapy's own SQL counts included, would see duplicates, which is §3's counterargument. Beside the table, they see exactly DEFAULT. EnableArchiving uses the same history table, without branches.
2. CreateVersion, AlterVersion, DeleteVersion, `da.ListVersions` and `da.Version`. Cursors and `da.Editor` take a
   version. Historical moments are read with `env` or a cursor's documented `datum` and moment arguments.
3. ReconcileVersions with the documented conflict definitions (by object, by attribute) and resolutions (favour
   edit version, favour target), then PostVersion.
4. Traditional versioning, replicas and enterprise administration stay refused, by name.
5. **INFERRED, for confirmation:** branch-only, the column names, and a version being a row in `grapy_versions`.

## 6. Consequences

- **State.** In the GeoPackage the tool works on: extra columns on a versioned class and a `grapy_versions` table.
  Nothing in the catalogue.
- About sixty refused names become buildable.

## 7. Assumptions this decision rests on

- Time-ordered moments within one process are monotonic enough to order edits. A moment is a counter, not a wall
  clock (INFERRED), so two edits in the same millisecond still order.

## 8. Dependencies

[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md), `da.Editor` (grapy's edit sessions).

## 9. Revisit triggers

- A user toolbox needs to write into a *server's* branch-versioned service. That is a server feature, not this.

## 10. Dissent

None recorded.

## 11. Conditions

1. **RegisterAsVersioned, CreateVersion, edits on a version, ReconcileVersions and PostVersion** work end to end,
   tested on a hand-worked conflict. **DISCHARGED 2026-10-30** by `test_versions.py`. A and B are edited on both sides: BY_OBJECT favouring the edit version gives (a, 10) and (bb, 2); BY_ATTRIBUTE favouring the target merges to (a, 20) and (bb, 30); ABORT_CONFLICTS changes nothing. Inserts and deletes post, and pruning removes exactly the superseded rows.
2. **EnableArchiving and historical reads** answer a past moment's rows. **DISCHARGED 2026-10-30**: after an update and a delete, ChangeVersion HISTORICAL at a time between them returns both original rows. TrimArchiveHistory keeps the current ones.
3. **Every remaining versioning and archiving name** is built or refused with its reason. **PARTLY DISCHARGED 2026-10-30**: traditional versioning and enterprise administration are refused (§5.4). Field conflict filters are refused until reconcile honours them. The top-level `arcpy.ListVersions` is not yet built.
