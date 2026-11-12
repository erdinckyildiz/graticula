# arcpy.parcel against ArcGIS Pro 3.7 — 2026-11-08

The 25 names grapy builds in `arcpy.parcel`, one case each (`cases/parcel.py`), run under Pro 3.7's arcpy and under
grapy. **First run: MATCH 15, DIFF 6, GRAPY-ERROR 1, PRO-ERROR 3. Now: MATCH 19, DIFF 5, PRO-ERROR 1.**

Each case builds its own fabric with the documented tools, in a workspace of its own (fabric class names are unique in
a geodatabase): a feature dataset in UTM 35N, CreateParcelFabric, AddParcelType "Lot", records and parcels inserted in
an edit session (`da.Editor`, which Pro requires on a fabric's classes), and for most cases test_parcel.py's three
parcels A, B, C with records R1, R2, R3 — here *without* Recorded Dates. A fabric's contents are answered as JSON, a
record's global ID as its name.

The *First run* column is the first comparison after the case's own faults were corrected: the topology cases read
`Result.maxSeverity`, which grapy's Result lacks (arcpy_top's open difference 1, the message model — the case now
counts the warnings the call adds), and two cases' attempt to delete CreatedByRecord from an output failed in Pro
(ERROR 002557; outputs are now answered and deleted). Those two are PRO-ERROR in the first-run column.

Every fix is pinned in `src/grapy/tests/test_pro_sweep_parcel.py`; `test_parcel.py`'s links test, which expected an
error without history, was changed with a comment saying Pro was measured.

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| AddParcelType | MATCH | MATCH | — | a strata type: outputs and both classes' fields |
| AnalyzeParcelsByLeastSquaresAdjustment | MATCH | MATCH | — | adr250-parcel.py's weighted network (P1, P2 constrained): adjusted coordinates to the millimetre, the three classes' counts |
| AppendParcels | MATCH | MATCH | — |  |
| ApplyParcelLeastSquaresAdjustment | MATCH | MATCH | — | points, lines and parcel after the apply |
| BuildParcelFabric | DIFF | DIFF | recorded | every line and point is where Pro puts it, and every unshared line's record and direction match. With records that have no Recorded Date, which record a *shared* line or point takes, and which way a shared line runs, differ — and Pro is not stable between identical builds: the line between A and B was R2 in two runs and R1 in a third (Build, Export, Merge cases). See open difference 1. |
| CopyParcels | PRO-ERROR | PRO-ERROR | not comparable | Pro takes only a *parcel fabric layer* (ERROR 000840 for the fabric's path); arcpy has no tool that makes one outside a map (no MakeParcelFabricLayer), and the sweep opens no project |
| CreateParcelFabric | MATCH | MATCH | — |  |
| CreateParcelRecords | MATCH | MATCH | — | names compared without case ("D-1", "d-1" one record) |
| CreateParcelSeeds | MATCH | MATCH | — |  |
| DeleteParcels | DIFF | DIFF | recorded | the parcels, lines and points left match; only the direction of the shared line C kept (Build's difference) |
| DisableParcelTopology | MATCH | MATCH | — | warning on a disabled topology |
| DuplicateParcels | MATCH | MATCH | — |  |
| EnableParcelTopology | MATCH | MATCH | — | warning on a new fabric (enabled), none after a disable |
| ExportParcelRecordFeatures | DIFF | DIFF | fixed (partly) | the exported Records class carries ExportedStatus, null, in Pro; grapy wrote the class without it — fixed. The points exported for R2 still differ by one, the shared point (100,0), whose record is Build's difference. |
| ExportSequencedParcelFeatures | MATCH | MATCH | — | NE, "P#", "ABC-##1" |
| FindAdjacentParcelPoints | PRO-ERROR | MATCH | fixed | case (above); then Pro's points carry Preserve 0 on every new point and Is Fixed 0 on the points Build makes — grapy left both null. Fixed (Preserve rule, Build). |
| FindDisconnectedParcelPoints | PRO-ERROR | MATCH | fixed | as FindAdjacentParcelPoints; NO_CONSIDER_EDGE and CONSIDER_EDGE both match |
| FindGapsAndOverlaps | MATCH | MATCH | — |  |
| GenerateParcelFabricLinks | GRAPY-ERROR | MATCH | fixed | Pro runs on a file geodatabase's fabric, whose points keep no history, and writes both outputs empty ("Row Count = 0", whatever the From date); grapy raised ERROR 000800. Now empty outputs with a warning. Each output's one field is SOURCE_GUID (Guid) in Pro; grapy wrote PointID — now SOURCE_GUID. |
| ImportParcelFabricPoints | MATCH | MATCH | — | proximity 1 m, a match moved, two created; lines and parcels follow |
| MergeCollinearParcelBoundaries | DIFF | DIFF | fixed (partly) | two lines with no COGO dimension merge with COGO Type left null in Pro; grapy wrote Computed — now Computed only when the merged line has a dimension. The rest is Build's difference (the A–B line's record). |
| RemoveParcelType | DIFF | MATCH | fixed | Adding the type again after removing it: Pro leaves the old classes and makes Tax_1 and Tax_Lines_1 (probe); grapy refused (000258). Pro answers "" for the two class outputs then; grapy answers the paths. |
| SelectParcelFeatures | MATCH | MATCH | — |  |
| SetParcelLineLabelPosition | DIFF | DIFF | recorded | every label matches; the shared line C/B runs the other way (Build's difference), so its row differs |
| ValidateParcelFabric | MATCH | MATCH | — | the topology errors exported (a parcel with no lines) |

## Fixes

- `_parcel_io.export_record` — ExportedStatus on the exported records class.
- `_parcel_io.links` — empty outputs (and a warning) without history; SOURCE_GUID.
- `_parcel.add_type` — suffixed classes where the names are taken.
- `_parcel.PRESERVE` — the Preserve calculation rule answers 0 where Preserve is empty (cursor inserts and Build).
- `_parcel_build.build` — Is Fixed 0 on the points Build makes.
- `_parcel_build._merge` — COGO Type Computed only with a COGO dimension.

## Open differences worth a debt row

1. **Build's record and direction for shared lines and points when records have no Recorded Date.** grapy takes the
   record with the latest Recorded Date, then the latest created (its docstring; measured with dated records on
   2026-10-31); Pro's choice with undated records is not documented and not even stable between identical builds here. Worth
   measuring with more parcels before choosing a rule; not aligned now. Estimate: half a day to probe, more if Pro
   stays unstable.
2. **CopyParcels is not comparable** from arcpy: Pro needs a parcel fabric layer made in a map. Comparing it means
   driving Pro with a project (not allowed in this sweep).
3. ImportParcelFabricPoints' created points were compared by name and place only; whether Pro writes Is Fixed 0 on
   them, as Build does, was not measured.
