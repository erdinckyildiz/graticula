# arcpy.cartography against ArcGIS Pro 3.7 — 2026-11-08

All 9 names grapy builds (6 in `cartography.py`, 3 imported from `_cartomore.py`), one case each
(`cases/cartography.py`), on the shared fixtures plus inputs the case module builds the same way on both sides: a 4 × 4
grid of squares sharing edges, three buildings (a notched rectangle, an L, a 4 m shed), a road drawn as two casings
20 m apart with a bend, and a two-segment line for strip-map pages. Further questions were asked of Pro in a probe
(TrimLine variants, SimplifyBuilding's `_Pnt`, CollapseDualLines on straight casings, partitions of 1,200 points).

**First run: DIFF 8, SCHEMA 1. Now: MATCH 4, DIFF 5 (all recorded).**

Pro accepted every case on the first run. The fixes are pinned in `src/grapy/tests/test_pro_sweep_cartography_edit.py`;
tests that pinned the old answers (`test_cartography.py` SimLnFlag and the unpaired line, `test_carto_lookagain.py`
PrevPage/NextPage) were changed, each with a comment saying Pro was measured.

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| AggregatePolygons | DIFF | DIFF | recorded | Same three output polygons and the same OUTPUT_FID/INPUT_FID table; the joined polygon is 3.3 % off in area. Pro bridges gaps with straight segments between the outlines; grapy's EQUIVALENT is a morphological closing with round joins. Pro's bridging is not specified. |
| CalculateColorTheoremField | DIFF | DIFF | recorded | Both colourings are valid (no two edge-adjacent squares share a value). Pro used 3 values on a 4 × 4 grid (counts 5/7/4), grapy's greedy Welsh–Powell 2. Pro's choice of values is not documented; grapy's docstring already says its values will differ. |
| CollapseDualLinesToCenterline | DIFF | DIFF | fixed in part; rest recorded | Fixed: LeftLn_FID/RightLn_FID were swapped on a bent road (grapy judged the side against the centreline's chord, which a bend can carry across the casing; now at the centreline); the centreline now reaches the middle of the pair's closing ends, as Pro's does; a line with no other line within the maximum width is not written (Pro left it out in both runs). Still different: Pro's centreline is a few vertices at the casings' corresponding vertices and segment midpoints, grapy's is the densified medial axis (1.4 m apart at the bend); Pro's direction is not always the first casing's; of a pair of unequal length (100 m and 60 m, 15 m apart) Pro wrote only the shorter, reversed, as LnType 2. No rule is documented for these. |
| CreateCartographicPartitions | DIFF | MATCH | fixed | Pro's first cell is the square on the centre of the inputs' shapes' extent, side the longer extent (grapy used the counted points' extent), and it writes RANK (the quadtree depth: 0, then 1 for quarters) and STATUS (null). Measured on the fixture and on 1,200 points split into four. |
| GenerateContiguousCartogram | DIFF | DIFF | recorded | Same polygons and fields; shapes differ by up to 15 % in area (30× the tolerance). Both follow the cited papers; Pro's grid size, padding and blur are not documented, grapy's are its own (INFERRED in the docstring). |
| SimplifyBuilding | DIFF | MATCH | fixed | Pro writes InBld_FID (the input's ObjectID) before BLD_STATUS; a building that is already a rectangle is unchanged (BLD_STATUS 5), not replaced by its rectangle (2); and the second output is the `_Pnt` path even under NO_KEEP, though the feature class is not created. |
| SimplifyLine | SCHEMA | MATCH | fixed | SimLnFlag is written only with `error_option="FLAG_ERRORS"` (probed: Pro writes it then, with the same flags as grapy); MaxSimpTol before MinSimpTol, Pro's order. |
| SimplifyPolygon | DIFF | MATCH | fixed | An outer ring that Douglas–Peucker would collapse keeps three vertices (Pro turned a 4 m square at 3 m into a triangle of three corners; grapy removed it), chosen as edit.Generalize chooses them; SimPgnFlag only with FLAG_ERRORS (probed); MaxSimpTol before MinSimpTol. |
| StripMapIndexFeatures | DIFF | DIFF | fixed in part; rest recorded | Fixed: the fields are Previous and Next (the page calls them PrevPage/Previous and Next in different places; Pro writes Previous, Next). Still different: page placement (Pro's first page starts 10 m before the line, half the 10 % overlap of a 200 m page, and its pages' angles are not grapy's — 22.4° against 26.4° on the bend); GroupId is text in Pro and Integer in grapy, and the page says integer; Angle is 337.6 in Pro where grapy writes −26.4 (the page's own convention is −90 for west, so Pro's 0–360 form is not copied). |

## Open differences worth a debt row

- **AggregatePolygons**: Pro's bridging between outlines (straight segments) against grapy's morphological closing —
  a different construction; aligning it needs Pro's bridging rule, which is not documented (days).
- **CollapseDualLinesToCenterline**: the centreline's vertex construction, its direction, and what Pro does with a
  pair of unequal length — undocumented; would need several more probes to infer (a day or two).
- **StripMapIndexFeatures**: Pro's page placement and angle — undocumented; GroupId's type (Pro: text, page: integer)
  and Angle's range (Pro 0–360, page −90 for west) are Pro disagreeing with its own page.
- **GenerateContiguousCartogram** and **CalculateColorTheoremField**: equivalents whose undocumented choices differ
  from Pro's; recorded, no fix planned without a specification.
