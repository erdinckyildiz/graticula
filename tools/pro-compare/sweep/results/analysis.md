# arcpy.analysis against ArcGIS Pro 3.7 — 2026-11-08

All 35 names grapy builds, one case each (`cases/analysis.py`), run under Pro 3.7's arcpy and under grapy on the shared
fixtures. **First run: MATCH 21, DIFF 8, SCHEMA 3, GRAPY-ERROR 3. Now: MATCH 35.**

The first run is the first one Pro accepted every case in: AssignWeightsByPairwiseComparison's case was PRO-ERROR
before that (it gave the comparisons as `"a b 3;…"`, which Pro refuses; the page's own form starts with the word
`Variables`). Split and SplitByAttributes were GRAPY-ERROR in the case's own read-back, not in the tools (see
*Outside these names*); with that fixed, Split was a DIFF and SplitByAttributes a MATCH.

After the harness fix of 69bfebb2 (an answer beside an output is now compared), PairwiseDissolve and SummarizeWithin
turned DIFF: Pro's result holds a second output, an empty string, for an unset optional output table; grapy's held one.
Both are fixed and counted in *Now*; their *First run* column keeps the verdict the old harness gave.

The fixes are pinned in `src/grapy/tests/test_pro_sweep_analysis.py`; three tests in `test_analysis_more.py` that pinned
the old answers were changed, each with a comment saying Pro was measured.

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| ApportionPolygon | MATCH | MATCH | — | |
| AssignWeightsByPairwiseComparison | GRAPY-ERROR | MATCH | fixed | grapy did not read the page's documented `"Variables a b 3; …"` form (KeyError). Pro refuses the form without `Variables`, which grapy still accepts; Pro accepts a list `[["a","b",3]]` and ignores it (equal weights) — recorded, not copied. |
| Buffer | SCHEMA | MATCH | fixed | Pro writes `BUFF_DIST` (in the data's units: "30 Feet" is 9.144018288 m, US survey) and `ORIG_FID` under dissolve NONE; nothing under ALL; the dissolve fields only under LIST. |
| Clip | MATCH | MATCH | — | |
| CountOverlappingFeatures | DIFF | MATCH | fixed | Pro writes `ORIG_NAME` (the input's path) with one input too, and writes the pieces covered once first (by count, then by the covering features' ObjectIDs). The case compares `ORIG_NAME`'s last part only. Without an overlap table Pro's result is `(out, "")` (measured by a probe; grapy answered `(out,)`) — fixed too. |
| CreateThiessenPolygons | DIFF | MATCH | aligned | "Extent plus 10%" is 5% on each side, not 10% (the INFERRED note now gives the measurement; Pro's frame was 1.0999 × the points' extent, 3 cm in 540 m unexplained). |
| Erase | MATCH | MATCH | — | |
| Frequency | MATCH | MATCH | — | |
| GenerateNearTable | MATCH | MATCH | — | |
| GenerateOriginDestinationLinks | DIFF | MATCH | aligned | `COLOR_ID` is not random in Pro, though the page says so: every link of the n-th origin is `(n − 1) % 8 + 1`, and an origin's links are in destination ObjectID order. |
| GraphicBuffer | MATCH | MATCH | — | already wrote `BUFF_DIST` and `ORIG_FID` (also measured with a distance field). |
| Identity | DIFF | MATCH | fixed | Pro cuts polygons of the input that overlap each other apart (the overlap once for each) and writes 0 / '' — not null — in the fields of the side that does not cover a piece. `ONLY_FID` keeps the input's fields and only the identity features' FID. |
| Intersect | DIFF | MATCH | fixed | Pro cuts one input's own overlaps (two overlapping polygons give three pieces, the overlap twice); one input alone answers its own overlaps. Built as a planar overlay of all inputs' polygons. `join_attributes` (ALL / NO_FID / ONLY_FID) is now honoured — it was ignored. |
| MultipleRingBuffer | MATCH | MATCH | fixed | the case (dissolve ALL) matched; a probe with dissolve NONE showed Pro keeps the input's fields and writes `ORIG_FID` after the distance field, which grapy did not. |
| Near | MATCH | MATCH | — | |
| PairwiseBuffer | SCHEMA | MATCH | fixed | as Buffer. |
| PairwiseClip | MATCH | MATCH | — | |
| PairwiseDissolve | MATCH | MATCH | fixed | after the harness fix, DIFF: Pro's result is `(out, "")` — the unset `out_lineage_table`. grapy now answers the same and takes `out_lineage_table` by name, refusing it (the page does not give the lineage table's fields). |
| PairwiseErase | MATCH | MATCH | — | |
| PairwiseIntersect | MATCH | MATCH | kept | Pro does **not** cut one input's own overlaps here; PairwiseIntersect called Intersect, so it now takes the pairwise path explicitly. |
| PairwisePolygonNeighbors | MATCH | MATCH | — | |
| PointDistance | MATCH | MATCH | — | |
| PolygonNeighbors | MATCH | MATCH | — | |
| RemoveOverlapMultiple | SCHEMA | MATCH | aligned | Pro writes one `ORIG_FID` (the polygon's ObjectID in its own input), not an `FID_<input>` per input — measured with one input and with two; another input's fields are null there. |
| Select | MATCH | MATCH | — | |
| SpatialJoin | MATCH | MATCH | — | |
| Split | GRAPY-ERROR | MATCH | fixed | the case's read-back first; then a DIFF: Pro cuts the input's own overlaps, as Intersect does. |
| SplitByAttributes | GRAPY-ERROR | MATCH | — | the case's read-back only. |
| Statistics | MATCH | MATCH | — | |
| SummarizeWithin | MATCH | MATCH | fixed | after the harness fix, DIFF: Pro's result is `(out, "")` when no group table is asked for; grapy's was `(out,)`. |
| SymDiff | DIFF | MATCH | fixed | 0 / '' in the missing side's fields, as Union. |
| TableSelect | MATCH | MATCH | — | |
| TabulateIntersection | MATCH | MATCH | — | |
| Union | DIFF | MATCH | fixed | as Intersect (own overlaps cut, one input alone accepted) and 0 / '' for the missing side. |
| Update | DIFF | MATCH | fixed | an update polygon writes 0 / '' in the input's fields it lacks, not null. |

## What else carries these

- The 0 / '' fill is one helper (`_blank`) used by every overlay that writes a missing side: Union, Identity, SymDiff,
  Update, and the line/point path of Intersect.
- The self-cut (`_planar_overlay`, `_self_cut`) is used by Intersect, Union, Identity and Split for polygon inputs;
  inputs of lines or points still take the pairwise path, which does not cut one input's own overlaps (not measured).
- `BUFF_DIST` / `ORIG_FID`: Buffer and PairwiseBuffer share the code; GraphicBuffer already had it; MultipleRingBuffer
  fixed on its own. No other grapy module calls these tools.

## Outside these names

- grapy's `arcpy.Describe` / `ListFields` do not resolve a bare dataset name against `env.workspace` when the workspace
  is a GeoPackage (`'z1' is not a GeoPackage feature class`); Pro's do. The Split cases read back with full paths.

## Open differences worth a debt row

- Intersect, Union, Identity and Split with **line or point** inputs still take the pairwise path, so one input's own
  overlaps are not cut there. Not measured in Pro; about half a day to measure and to extend the planar overlay.
- Union's `gaps` (NO_GAPS) is still accepted and ignored. Small once measured.
- CreateThiessenPolygons: Pro's frame is 1.0999, not 1.1, times the points' extent (3 cm in 540 m); cause unknown.
