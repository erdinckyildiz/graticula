# ADR-242 — grapy builds Topo to Raster, Segment Mean Shift and the combinatorial tools

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the combinatorial tools, which follow a worked example Esri publishes · `MEDIUM` for the equivalents (Topo to Raster after Hutchinson, Segment Mean Shift after Comaniciu and Meer, the segment attributes and the tiling repair), which are published methods but not Esri's code, and whose numbers will differ from Pro's |
| **Decided** | 2026-10-31, by owner decision on scope (round 2 of ADR-224's sweep: *"go on until everything is done, then go back to the start and look at the rest once more"*): refusals that carry a reason are looked at again, and a reason that a related page or a published method answers is removed. The choices §5.4 lists are INFERRED and listed for confirmation in [Q-211](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Before this decision grapy refused, in `arcpy.sa`, `arcpy.ia` and `arcpy.ddd`, eighteen names (thirty-six
module entries) with reasons a second reading did not support:

- **TopoToRaster and TopoToRasterByFile** (sa, ddd) — *"its algorithm is described in the documentation but not
  specified closely enough to reproduce"* — and the nine input classes TopoBoundary, TopoCliff, TopoCoast, TopoContour,
  TopoExclusion, TopoLake, TopoPointElevation, TopoSink and TopoStream (sa, ia) as *"an input class of TopoToRaster"*.
- **SegmentMeanShift and SegMeanShift** (sa, ia), *"not specified closely enough"*, and ComputeSegmentAttributes and
  RemoveRasterSegmentTilingArtifacts (sa, ia) because they *"work on the segmented rasters of SegmentMeanShift"*.
- **CombinatorialAnd, CombinatorialOr and CombinatorialXOr** (sa, ia): *"the page does not say how the unique
  combination values are numbered, nor how NoData is treated"*.

What the public pages say (doc.esri.com, which pro.arcgis.com redirects to, read 2026-10-31; no Esri implementation
was read, CLAUDE.md §5):

- **The combinatorial tools are fully defined — by a page the first reading missed.** Each tool page links *Learn more
  about how Combinatorial tools work* (`h-how-combinatorial-math-tools-work`). That page says NoData in either input is
  NoData; the remaining cells are scanned left to right and top to bottom, an internal table recording each unique pair;
  the condition is applied; *"The False condition will return a value of 0. Each unique True condition will be returned
  as a sequential number from the first one encountered"*; and the attribute table gains two items holding the input
  values. It works a 4 × 4 example through to every output value for all three tools. The arcpy pages for the three
  answer 404; the tool-reference pages carry the Python syntax.
- **Topo to Raster names its algorithm and its sources.** *How Topo to Raster works* says it is ANUDEM (Hutchinson 1988,
  1989, 1996, 2000, 2011) — iterative finite difference interpolation, *"essentially a discretized thin plate spline
  technique"* with a modified roughness penalty, multi-resolution, drainage enforcement through *"the lowest saddle
  point"* subject to two tolerances, streams, cliffs, lakes, coasts and the diagnostic outputs and their codes. The tool
  page gives every parameter and default; the by-file page gives the parameter file's syntax with an example; the nine
  class pages give `inFeatures`. Hutchinson et al. (2009, MODSIM) and Hutchinson, Xu and Stein (2011, Geomorphometry),
  both public, give the fitting criterion (their eq. 2 and 3), the multi-grid method, and the drainage enforcement
  algorithm in words. ANUDEM's code is not public, and the equivalent below does not claim to be it.
- **Segment Mean Shift names its method.** *Understanding segmentation and classification* says *"The image
  segmentation is based on the Mean Shift approach"* and cites Comaniciu and Meer (2002). The SegMeanShift function's
  page gives `spectral_radius` and `spatial_radius` as the inverse of the detail parameters over 0–21. Neither page
  gives the tool's defaults for spectral detail, spatial detail and minimum segment size.
- **Compute Segment Attributes** gives the attributes and their ranges (compactness 1 for a circle, rectangularity 1
  for a rectangle) and the default set, not their formulas or the table's field names. **Remove Raster Segment Tiling
  Artifacts** says only that it corrects segments cut by tile boundaries, 512 pixels by default.

## 2. Alternatives considered

### Alternative A — build the combinatorial tools to the page and the rest as cited equivalents (chosen)

**For.** The combinatorial numbering is Esri's own, tested on Esri's own example; the other four tools become usable
under arcpy's names, each naming its published method and warning once per run that its numbers may differ (ADR-224
§5.6). No new wheel: numpy, scipy (sparse direct solve, connected components) and shapely do the work.
**Against.** An equivalent will be compared with Pro and differ; Topo to Raster in particular is a large program whose
many locally adaptive refinements the 2009 and 2011 papers describe only in outline.

### Alternative B — keep the refusals

**For.** Nothing is claimed that is not Esri's. **Against.** ADR-224 §5.6 already decided that an undocumented
algorithm is built as a cited equivalent; *"not specified"* is now the case for an equivalent, not a reason, and the
combinatorial reason was simply false.

### Alternative C — adopt ANUDEM, or a mean shift library

**For.** ANUDEM itself is the reference. **Against.** ANUDEM is distributed by ANU under a commercial licence, not as
source a fenced user tool may link; OpenCV's `pyrMeanShiftFiltering` is a heavy dependency and not in the image; and the
computations are Tier 1 domain (§4), not a library behind a port.

## 3. Counterarguments to the preferred option

- The Topo to Raster equivalent solves a sparse system directly, and chooses λ by bisection — two dozen solves per
  resolution on grids up to 40,000 cells, one solve per enforcement iteration beyond. It is fine at a few hundred cells
  a side and unmeasured beyond ([D-333](../architecture-debt.md)).
- It does not derive stream and ridge lines from contour corners (Hutchinson 1988), which is how ANUDEM gets shape from
  contours; a cone is recovered within a quarter of the contour interval without them, but a valley drawn only by
  contours will be shallower than Pro's.
- Mean shift on large images is slow in numpy (the window is looped per offset), and the segment colours, which
  downstream tools key on, will not equal Pro's.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The combinatorial numbering is the page's | The page's 4 × 4 example reproduced cell for cell for And, Or and XOr, and its counts in the parentage table | `test_topo_segment.py` Combinatorial |
| Topo to Raster recovers a surface from contours | A cone z = 100 − 0.4r sampled as nine contours, 10 apart: every cell between the top and the outer contours within 2.5 of the truth (measured: 1.20 at most, 0.27 on average) | TopoToRaster.test_a_cone… |
| Drainage enforcement removes a spurious sink, and the tolerances hold | A plane of spot heights with one 8 lower: with NO_ENFORCE the cell beside it is a sink; with ENFORCE and tolerance_1 10 no sink is left anywhere and a code-3 chain is written; with spot data's default tolerance_1 of 0 the sink stays and is reported at the low spot | TopoToRaster.test_drainage… |
| Streams descend, cliffs break and are checked, lakes are flat | A stream drawn uphill is pinned strictly descending; a cliff drawn the wrong way is flagged code 8 along its length, drawn the right way only beside the low spot | TopoToRaster.test_stream… |
| The parameter file is the documented syntax | A run's parameter file read back by TopoToRasterByFile answers the same raster to 1e-3, and so does arcpy.ddd's value-table form | TopoToRaster.test_parameter… |
| Segmentation separates, merges and divides | Two noisy colours become two segments in their average colours within a grey level; a 4-pixel blob joins its nearest-coloured neighbour under a minimum of 5; a uniform 8 × 8 divides into four 16-pixel parts under a maximum of 16 | Segmentation |
| The attributes are right on shapes with known answers | A square segment: rectangularity 1, compactness 2/π; means 1 and 3 and standard deviation 0.5 from a striped raster | Segmentation.test_compute… |

## 5. Decision

### 5.1 Built to the pages

CombinatorialAnd, CombinatorialOr and CombinatorialXOr, in `arcpy.sa` and `arcpy.ia`, exactly as *How the
combinatorial math tools work* defines them; a floating-point input is refused, as the pages require integers; two
constants need the cell size and extent environments, as the pages say. The nine Topo classes are built to their class
pages: each holds `inFeatures`.

### 5.2 Built as equivalents (ADR-224 §5.6)

- **TopoToRaster and TopoToRasterByFile** (sa, ddd): ANUDEM after Hutchinson (1989), Hutchinson et al. (2009, §2.1) and
  Hutchinson, Xu and Stein (2011), with the priority flood of Barnes, Lehman and Mulla (2014) to find each sink's
  lowest saddle. Every parameter and default of the page is honoured; `grapy/_topo.py`'s docstring states the method.
  arcpy.ddd's value table `"path field Type; …"` is read as well as the Topo objects.
- **SegmentMeanShift and SegMeanShift** (sa, ia): Comaniciu and Meer (2002) §4.1–4.2 — joint-domain filtering with a
  flat kernel, delineation, small-region merging — with `h_r = 21 − spectral_detail` and `h_s = 21 − spatial_detail`
  from the function page's radii.
- **ComputeSegmentAttributes** (sa, ia): COLOR, COUNT, MEAN, STD as the page defines them; compactness as area over the
  smallest enclosing circle's (MacEachren 1985) and rectangularity as area over the minimum rotated rectangle's (Rosin
  1999), the two the page gives only by range.
- **RemoveRasterSegmentTilingArtifacts** (sa, ia): segments meeting across a seam every `tileSizeX` columns and
  `tileSizeY` rows are joined when every edge they share lies on a seam and their colours agree within 5.5 grey levels
  per band, Segment Mean Shift's default spectral radius.

### 5.3 Refused

None of the eighteen names. Within them, by name: Topo to Raster's `DISPLAY` extent (a fenced tool has no map);
SegMeanShift's `astype` other than 8-bit (the page lists no pixel types); every environment setting
`_refuse_environment` already refuses for grapy's raster tools.

### 5.4 INFERRED choices (listed in Q-211) — **Confirmed 2026-10-31 by owner decision in Q-211** (*"Hepsini onayla"*)

(a) **Combinatorial**: the parentage fields named after the inputs as Combine names its fields, `constant1` or
`constant2` for a number, and a row for 0 holding the false cells' count with null parentage.
(b) **Topo to Raster's fit**: J = (1 − p)·Σ(f_xx² + 2f_xy² + f_yy²) + p·Σ(profile curvature²) + r·Σ(first
differences²), differences in grid units — the 2009 paper names *curvature and potential* for spot data and *minimum
curvature augmented by a locally adaptive minimum profile curvature penalty* for contours without equations; λ by
bisection on its logarithm until the weighted residual sum of squares equals the data count, carried ×4 to a finer grid
of more than 40,000 cells; the vertical standard error and discretisation error combined as √(σ² + (k·h·s)²/12),
floored at 10⁻⁴ of the data's range; the first grid's slope the data's range over the grid's diagonal; resolutions
halving from a grid whose shorter side is at least 8 cells, at most five times; contours sampled every quarter cell; a
point and a contour sample in one cell averaged together; data in lakes, exclusion areas or outside the z limits
unused; a sample across a cliff from its cell's centre dropped.
(c) **Drainage enforcement**: a sink is a cell lower than all eight neighbours, not on the grid's edge, not a lake
cell, not a known sink under ENFORCE_WITH_SINK; under ENFORCE a sink input with a field is elevation data and is cleared
like any other; the chain is pinned up to the saddle at the saddle's height and beyond it only where it lowers, the
step 10⁻⁶ of the data's range; clearing runs in place until no sink yields, then the next iteration re-solves round the
pinned chains; `maximum_iterations` counts those re-solves at each resolution.
(d) **Topo to Raster's outputs**: the coast's special value the minimum z limit less 1; z limits' defaults of 20
percent read as 20 percent of each value's magnitude; the output clamped to the z limits; lakes flattened to the lowest
shore cell after the fit (the tool page's rule, not the revised lake algorithm of *How it works*, which the 2011 paper
outlines without equations); field names `Code`, `Elevation`, `Residual`, `Scaled`, `ErrorValue`, `Bias`; residuals
written where the scaled residual exceeds 1; contour bias points with ErrorValue 2; the diagnostic file's layout; the
stream codes 4, 5, 7 and 9 and the error codes 2, 3, 6 and 10–13 never written, the equivalent not deriving lines from
contour corners nor detecting those conditions; no failure at 8,000 sinks or at more points than cells, which are
Pro's limits; a coastal polygon written `Coast <Path>` in the parameter file, where the page's table writes `Boundary
<Path>`; a path with spaces in double quotes.
(e) **Segment Mean Shift**: the defaults 15.5, 15 and 20, which no page states; bands 1, 2, 3 of a raster of more than
three; non-8-bit data stretched from its minimum to its maximum; convergence at a tenth of a pixel and a grey level, or
twenty iterations; delineation over 4-neighbours; small regions merged smallest first, ties to the lower label; a
segment over the maximum split at the median along the longer side of its box; the colour the segment's average, from
the input's own (stretched) values; SegMeanShift's radius winning over its detail when both are given.
(f) **The segment tools**: a segment is a 4-connected region of one colour, numbered in scan order; fields `Red`,
`Green`, `Blue` (`Gray`), `Count`, `Mean_<band>`, `Std_<band>`, `Compactness`, `Rectangularity`; STD the population
deviation; the additional raster on the segmented raster's grid; the seams counted from the top-left corner.

## 6. Consequences

- **State.** None: everything is computed in the run.

**Positive.** A user tool can grid contours and spot heights into a drained DEM with streams, cliffs, lakes and
boundaries, segment an image, measure its segments and repair a tiled segmentation, and number combinations, on this
server under arcpy's names. No new dependency.

**Negative.** Pro's numbers will differ wherever an equivalent stands, and Topo to Raster's cost on real grids is
unmeasured ([D-333](../architecture-debt.md)).

**Names.** `tools/grapy-refusals.json` before → after: `arcpy.sa` built 208 → 226 (equivalents 21 → 27), refused 170 →
152; `arcpy.ia` built 206 → 222 (equivalents 29 → 33), refused 141 → 125; `arcpy.ddd` built 104 → 106 (equivalents
8 → 10), refused 52 → 50; all modules built 1304 → 1340 (equivalents 156 → 168), refused 870 → 834, unexamined 0 → 0.

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A direct sparse solve is fast enough for the grids a user tool makes | Unverified beyond 80 × 80 (D-333) |
| — | Hutchinson's published criterion, with the INFERRED functional, behaves like ANUDEM on ordinary data | Tested on synthetic surfaces only (condition 3) |

## 8. Dependencies

**Depends on**: [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.6 (equivalents),
[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) (the fence and its limits); clean room (CLAUDE.md §5): only
public pages and published papers were read.

**Depended on by**: —

## 9. Revisit triggers

- A Pro run of the same inputs (condition 3) differs beyond what the equivalents explain.
- ANUDEM's equations, or Esri's segment attribute formulas, are published.
- A user tool reaches ADR-223's time limit in TopoToRaster or SegmentMeanShift.

## 10. Dissent

None recorded.

## 11. Conditions

1. **Every name of the eighteen is built, an equivalent, or refused with its own reason.** **DISCHARGED
   2026-10-31**: `tools/grapy_refusals.py --check` passes with none unexamined, and none of the eighteen is refused.
2. **The tools answer worked or hand-computed values.** **DISCHARGED 2026-10-31**: `test_topo_segment.py`, fifteen
   tests (§4), the combinatorial ones on Esri's own example.
3. **The same contours and spot heights are run through Pro's Topo to Raster and grapy's**, and the same image through
   Pro's Segment Mean Shift, and the differences recorded beside the time each took on a grid of real size. **PARTLY
   DISCHARGED 2026-10-31.** *Topo to Raster* — Pro 3.7 and grapy on `tools/pro-compare/adr242-topo.py`'s analytic
   2,000 m square (two hills on a tilted plane with a valley, 200–420 m), 29 contours every 10 m and 120 spot heights:
   on 100 × 100 cells of 20 m they differ by 0.41 m on average and 2.7 m at most; on 200 × 200 cells of 10 m by 0.39 m
   and 2.9 m. Both are as near the true surface as each other (mean 0.43 and 0.43 m at 20 m; 0.30 m for Pro and 0.45 m
   for grapy at 10 m). Pro took 2.6 s and 4.1 s, grapy 11.2 s and 13.5 s. A cited equivalent within a fraction of the
   contour interval: kept. *Segment Mean Shift* — on `tools/pro-compare/adr242-segment.py`'s 160 × 160 3-band image
   (sixteen patches with a red gradient, a disc, noise σ 6), with the defaults 15.5, 15, 20: Pro 181 segments, grapy
   859, adjusted Rand index 0.04; at spectral detail 10 they agree (153 and 151 segments, Rand 0.99). On the image's
   first band alone Pro gives 813 segments at 15.5 and grapy 18, so Pro's spectral scale is neither §5.4 (e)'s
   `h_r = 21 − spectral_detail` nor one factor of it: coarser than it on colour, finer on grey. Pro took 37–77 s, grapy
   1.4–9.8 s. *Measured further 2026-10-31:* Pro's threshold was measured on 48 × 48 images of two flat halves (`tools/pro-compare/adr242-segment-step.py`, 23 runs at spectral detail 15.5): on one band two halves stay apart from a difference of 4 grey levels at 100 (3 merges), of 4 at 220 (2 merges), but merge at 3 at 160 and at 4 at 30 and 60, and separate only from 7–8 there — so the threshold depends on brightness, between 4 and 8 levels in the dark (30, 60) and between 2 and 4 in the bright (100, 220), which neither sRGB L* (whose steps are largest in the dark) nor linear light fits; and a three-band image whose bands all step by 4 at 100 merges where the same step on one band separates, while a step of 10 in the first band alone still merges. So Pro converts or weights the bands before measuring, in a space its outputs bracket but do not name, at about two minutes a run. Speed and size: **Measured 2026-10-31** (this machine, other work running; `tools/pro-compare/adr242-topo.py` at 400 and 800 cells a side, `tools/pro-compare/adr242-segment-time.py`): Topo to Raster on the 2,000 m square took 13.5 s at 200 × 200, 50.5 s at 400 × 400 and 486 s at 800 × 800, on these 149 inputs (the limit follows the number of inputs too: D-333's real-data runs reach it at 400 × 400 with 10,000 heights); Segment Mean Shift on a 3-band 8-bit image with the defaults took 15–21 s at 320 × 320, 254 s at 640 × 640 and 731 s at 1,280 × 1,280, with 1.37 GB allocated by Python at the last, past the ten minutes, so about 1,000 pixels a side fit. Its region adjacency and renumbering, which looped over every pixel and pair in Python (57 of 93 s at 320 × 320 under load), are now numpy with identical output; the filtering itself is the rest. A larger grid or image fails by ADR-223's limit, by name (D-333). Not fixed in this pass; [D-336](../architecture-debt.md). **Measured further 2026-10-31, a stretch ruled out** (`tools/pro-compare/adr242-segment-stretch.py`): if Pro scaled each band by its own minimum and maximum, or by its standard deviation, before the range kernel, a 6 × 6 patch of 255 in a corner of the two-halves image would shrink the halves' difference by the patch's range and change their answer; in Pro it changes nothing — 100 against 106 and 30 against 40 stay apart, 100 against 102 stays one segment, with and without the patch. The bracketing above already ruled out any per-image stretch of a two-level image, since such a stretch maps every difference to the same one, yet Pro's answer flips between 3 and 8 levels. What is left is a brightness-dependent transform of the values themselves, which neither sRGB L* nor linear light is; the condition stays partly discharged at that, and so does D-336 (2). **DISCHARGED 2026-10-31** — compared; the remaining difference accepted by owner decision ("Kabul et, borçta izle") and tracked in [D-336](../architecture-debt.md) with its measurement and trigger.
4. **The owner confirms or changes §5.4** ([Q-211](../open-questions.md)). **DISCHARGED 2026-10-31** — confirmed as built by owner decision in Q-211 (*"Hepsini onayla"*).
