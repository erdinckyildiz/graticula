# arcpy.edit against ArcGIS Pro 3.7 — 2026-11-08

All 16 names grapy builds, one case each (`cases/edit.py`). Every edit tool changes its input in place, so each case
copies its input to an `O()` output first and edits the copy. Inputs beyond the shared fixtures, built the same way on
both sides: four displacement links with a little shear, two map sheets meeting across a 4 m gap, the fixture's lines
moved (2, 4) m, dangles and overshoots. A probe asked Pro further questions (TrimLine with no length and with
KEEP_SHORT, a spur, interior rubbersheet links on lines whose vertices do not correspond, AlignFeatures on a longer
target).

**First run: MATCH 9, DIFF 5, SCHEMA 2. Now: MATCH 14, SCHEMA 2 (recorded).**

Pro accepted every case on the first run. The fixes are pinned in `src/grapy/tests/test_pro_sweep_cartography_edit.py`;
tests that pinned the old answers (`test_edit.py` TrimLine's tee, `test_edit_equivalents.py` the rubbersheet outputs)
were changed, each with a comment saying Pro was measured.

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| AlignFeatures | DIFF | MATCH | fixed | Where a target end lies within the search distance of an input end, Pro runs the aligned line to that target end; grapy stopped at the projection of the input's end. Probe: with a target 50 m longer at each end Pro fails (ERROR 999999, "operation attempted on an empty geometry") — recorded as Pro's defect; grapy keeps the stretch between the projections. |
| CalculateTransformationErrors | MATCH | MATCH | — | RMSE and residuals equal. |
| Densify | MATCH | MATCH | — | |
| EdgematchFeatures | SCHEMA | SCHEMA | fixed in part; recorded | The SCHEMA is the link class the case builds with GenerateEdgematchLinks (below); the edited lines match. grapy now reads Pro's TGT_FID links as well as ADJ_FID ones. |
| ErasePoint | MATCH | MATCH | — | |
| ExtendLine | MATCH | MATCH | — | The case uses `extend_to="FEATURE"`; the default EXTENSION stays refused in grapy (which of two meeting extensions is lengthened is not documented). |
| FlipLine | MATCH | MATCH | — | |
| Generalize | MATCH | MATCH | — | |
| GenerateEdgematchLinks | SCHEMA | SCHEMA | recorded | Pro writes the adjacent feature's id as **TGT_FID**; the page defines it as **ADJ_FID** (and mentions TGT_FID once, in a different sentence). Pro disagrees with its own page, so grapy keeps ADJ_FID and EdgematchFeatures reads either. Links and EM_CONF are equal. |
| GenerateRubbersheetLinks | DIFF | MATCH | fixed | Pro links every interior source vertex too, after the ends, to the point at the same fraction of length along the matched target (probe: a four-vertex source on a three-vertex target, to 1e-3 m); grapy linked the ends only. Outputs are (links, empty match table, identity links) as Pro's; grapy answered (links, identity links). |
| RubbersheetFeatures | MATCH | MATCH | — | |
| Snap | MATCH | MATCH | — | |
| TransferAttributes | DIFF | MATCH | fixed | Pro answers the (empty) match table before the target: `("", target)`; grapy answered `(target,)`. |
| TransformFeatures | MATCH | MATCH | — | |
| TrimLine | DIFF | MATCH | fixed | Pro deletes a line whose two dangles meet at an interior point (a 40 m line crossing another at its middle, dangle 25 m) under DELETE_SHORT, and keeps it whole under KEEP_SHORT; a line touching others only at one end and no longer than the dangle length is deleted under either option; with no dangle length, both kinds go (probes). grapy kept both whole. |
| UpdateCOGO | DIFF | MATCH | fixed | Distance is the straight span between the line's first and last vertex (Pro: 522.0 m for a 554.0 m four-vertex line), not the length. |

## Open differences worth a debt row

- **GenerateEdgematchLinks** writes ADJ_FID where Pro writes TGT_FID: Pro contradicts its page. If the owner prefers
  Pro's name, it is a one-line change (EdgematchFeatures already reads both).
- **AlignFeatures** with a target longer than the search distance at either end: Pro fails; grapy answers. Nothing to
  align to.
