# ADR-289 — The owner's four answers after the Pro sweep: Pro's column names, Pro's messages, Pro's attachment keys, and the page over Pro

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-12, **by owner decision**, in answer to four questions put at the end of the Pro sweep's overnight run, each with a recommendation (§2). The owner took three recommendations and chose against one (§2.1). How each answer is carried out is this ADR's. |
| **Supersedes** | — It amends [ADR-228](ADR-228-grapy-holds-subtypes-and-attachments.md) on attachment keys, [ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) §5 on messages, and the GeoPackage column names grapy has written since its first tool. It turns [D-409](../architecture-debt.md), [D-410](../architecture-debt.md) and [D-411](../architecture-debt.md) from questions into work. |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

On 2026-11-08 the owner asked for every arcpy name grapy builds to be compared with ArcGIS Pro 3.7. The sweep ran module
by module; its cases and results are under `tools/pro-compare/sweep/`. Most differences it found were fixed in grapy.
Four needed a decision, because each either touches every tool or reverses an earlier choice:

- Pro writes `OBJECTID` and `Shape` even in a GeoPackage. grapy writes `fid` and `geom`, which is what the GeoPackage
  specification's examples and GDAL write.
- Pro's GetMessages answers the last tool's messages, with "Start Time…" and "Succeeded at…". grapy keeps one list for
  the whole run.
- Pro relates attachments by `REL_OBJECTID` on a class without global IDs. grapy always adds global IDs and relates by
  `REL_GLOBALID` (ADR-228, INFERRED).
- Some tools behave against their own Esri page: Remap's `allow_unmatched` default, SAVI's default bands, VfSecCos's
  powers, GenerateEdgematchLinks' field name, Merge MEAN and others. grapy follows the page.

## 2. Alternatives considered

**(R)** marks the recommendation the question carried.

1. **GeoPackage column names** ([D-410](../architecture-debt.md)): keep `fid` and `geom` (R); or write `OBJECTID` and
   `Shape` as Pro does. **Answer: as Pro does.**
2. **Where Pro contradicts its own page:** follow the page (R); follow Pro; or follow Pro on field names only.
   **Answer: follow the page.**
3. **The message model** ([D-409](../architecture-debt.md)): build Pro's per-tool messages (R); or leave it.
   **Answer: build it.**
4. **Attachment keys** ([D-411](../architecture-debt.md)): `REL_OBJECTID` as Pro does (R); or keep global IDs.
   **Answer: `REL_OBJECTID`.**

## 3. Counterarguments to the preferred option

- **Column names.** `fid` and `geom` are what other GeoPackage readers expect, and the change reaches every grapy
  reader and writer. A GeoPackage that grapy wrote before the change has the old names, so grapy must go on reading
  both. Against that: a Pro script that names `OBJECTID` or `Shape` fails on grapy's output today, and grapy's purpose
  (ADR-223) is that Pro scripts run.
- **The page over Pro.** A script written and checked against Pro gets a different answer from grapy in these tools.
  Copying a defect would make grapy wrong for anyone who reads the page; each case is recorded in the sweep's results.
- **Messages.** Every tool call is wrapped, so a defect there is in every tool.
- **Attachments.** A global ID survives a copy and an object ID does not. Pro's choice, measured, is what scripts see.

## 4. Evidence

The sweep's results: [arcpy_top.md](../../tools/pro-compare/sweep/results/arcpy_top.md) (messages, environments, column
names), [da.md](../../tools/pro-compare/sweep/results/da.md),
[management_1.md](../../tools/pro-compare/sweep/results/management_1.md) and
[management_2.md](../../tools/pro-compare/sweep/results/management_2.md) (attachments, column names, unregistered tables),
[sa_2.md](../../tools/pro-compare/sweep/results/sa_2.md) and [edit.md](../../tools/pro-compare/sweep/results/edit.md) (Pro
against its page). All measured in ArcGIS Pro 3.7 between 2026-11-08 and 2026-11-12.

## 5. Decision

### 5.1 Column names — built

grapy writes a GeoPackage's object ID as `OBJECTID` and its geometry as `Shape`, as Pro does. It reads both the new
names and `fid`/`geom`, so a GeoPackage written earlier still opens. Describe, cursors, ListIndexes and every derived
name follow from the column, not from a constant.

### 5.2 The page over Pro — decided, nothing to build

Where Pro's measured behaviour contradicts its own Esri page, grapy follows the page and the sweep's results record
the difference. This is the rule from now on, not only for the tools named in §1.

### 5.3 Messages — built

GetMessages, GetMessage, GetMessageCount, GetMaxSeverity, GetSeverity and the Result's messages answer the last tool's
messages, with the start and end lines Pro writes. AddMessage outside a tool does not join them. What the server's job
answers (ADR-223) is kept.

### 5.4 Attachments — built

On a class without global IDs, EnableAttachments adds none and relates by `REL_OBJECTID` (Integer). A class that has
global IDs keeps `REL_GLOBALID`, as Pro does. Attachments that grapy wrote earlier, with `REL_GLOBALID`, are still read.

## 6. Consequences

- D-409, D-410 and D-411 cite this ADR. All four parts are built; what the rows still hold open is outside the four answers.
- The column change is the widest grapy change since the package began. Its tests are the sweep's cases, run again
  after the change.
- **State.** None in the catalogue or at runtime. The decisions change what grapy writes into a user's GeoPackage
  (column names, the attachment table's key) and what a script reads back; the server stores nothing new.

## 7. Assumptions this decision rests on

- Pro 3.7's behaviour measured by the sweep is what users' scripts were written against.

## 8. Dependencies

[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md), [ADR-228](ADR-228-grapy-holds-subtypes-and-attachments.md),
[ADR-240](ADR-240-grapy-holds-editing-behaviours-and-registration.md).

## 9. Conditions

1. **Column names built (§5.1)**: the sweep's arcpy_top, da and management cases match Pro on the names, and a
   GeoPackage written before the change still opens. **DISCHARGED 2026-11-12**: ListIndexes (arcpy_top), SearchCursor `"*"` (da) and AddJoin's `<table>_OBJECTID` (management_1) moved to MATCH; Describe's names, fields and field order now match Pro, and what stays DIFF there is outside §5.1 (a one-table GeoPackage as a Workspace, the extent's rounding). `test_gpkg_names` opens, edits and copies a `fid`/`geom` GeoPackage built by hand. `Shape_1`, used when copied data already has a field named Shape, is INFERRED.
2. **Messages built (§5.3)**: the sweep's message cases match Pro. **DISCHARGED 2026-11-12**: 24 of 24 message cases match Pro (18 were DIFF), three of them new (nested tools, levels, a Result's messages). One wrapper in `_runtime.run_tool` gives every tool its own messages with Pro's start and end lines; SetSeverityLevel 0 and 1 are built; the run's list the server's job answers (ADR-223) is unchanged.
3. **Attachments built (§5.4)**: the sweep's attachment cases match Pro, and attachments written earlier still read. **DISCHARGED 2026-11-12**: all eight attachment cases match Pro (four were DIFF); `_attachments.keys` reads the key from the table's columns, and `test_subtypes_attachments.ObjectIdKeyedAttachments` opens a GeoPackage written in the old layout. Pro refuses EnableAttachments on a GeoPackage (ERROR 000187), so Pro was measured on a file geodatabase.

## 10. Revisit triggers

- A GeoPackage reader outside grapy that is used with grapy's output fails on `OBJECTID` or `Shape`.
- Esri corrects a page or a tool so that the two agree.

## 11. Dissent

The recommendation for §5.1 was to keep `fid` and `geom`; the owner chose otherwise. That is recorded here, not argued
further.
