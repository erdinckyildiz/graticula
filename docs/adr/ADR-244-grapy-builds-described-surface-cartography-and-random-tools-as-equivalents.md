# ADR-244 — grapy builds the described-but-unspecified surface, cartography and seeded statistics tools as equivalents, and refuses the rest by name

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-31, by owner decision on method: *"go on until everything is done, then go back to the start and look at the rest once more."* This is round 2: the refusals that already carried a reason were read again against their pages. Where a published method does what the page describes, the name is built as an equivalent (ADR-224 §5.6). Where it does not, the refusal stays, reworded to say exactly what the page leaves out. The choices the pages leave open are INFERRED and listed for confirmation in §5.4 and [Q-213](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Thirty-three grapy names were refused with one of three blanket reasons:

| Group | Module | Reason before | Names |
|---|---|---|---|
| 1 | `arcpy.ddd` (10), `arcpy.sa` (8) | *its algorithm is described in the documentation but not specified closely enough to reproduce* | ContourWithBarriers, ObserverPoints, RegularizeBuildingFootprint, RegularizeAdjacentBuildingFootprint, SplineWithBarriers, StackProfile, SurfaceParameters, TopoToRaster, TopoToRasterByFile, Viewshed2, ZonalFill |
| 2 | `arcpy.cartography` (8) | *its procedure is described only in outline, not specified closely enough to reproduce* | CollapseHydroPolygon, CollapseRoadDetail, CreateCartographicPartitions, GenerateContiguousCartogram, GenerateHachuresForDefinedSlopes, MergeDividedRoads, StripMapIndexFeatures, ThinRoadNetwork |
| 3 | `arcpy.stats` (7) | *its answer depends on random draws — permutations, simulations, seeds or random splits — so the same call would not give arcpy's answer* | AttributeUncertainty, BuildBalancedZones, CrossValidate, LocalBivariateRelationships, PrepareData, PresenceOnlyPrediction, SpatiallyConstrainedMultivariateClustering |

The reasons were true of the tool page alone, and they were written in a batch. Round 2 found three faults in them.
First, a related page often defines what the tool page omits: *How Geodesic Viewshed works*, *How Surface Parameters
works*, *How Spline with Barriers works*, *Using Viewshed and Observer Points for visibility*, *How Local Bivariate
Relationships works*, *How Spatially Constrained Multivariate Clustering works*. Second, several pages name the
published method they implement: Briggs 1974, Minár et al. 2020, Gastner and Newman 2004, Gastner, Seguy and More
2018, Assunção et al. 2006, Guo 2010. Third, randomness by itself is no reason. grapy already seeds
(`management._seed()` reads `env.randomGenerator` and drives NumPy's PCG64), and no equivalent matches Pro bit for bit
anyway. The test is whether the tool does what its page says.

## 2. Alternatives considered

### Alternative A — build each name a published method answers, as an equivalent, and refuse the rest by name (chosen)

**For:** each built name follows its page's parameters, defaults and outputs, and its method has a citation the reader
can check. `_runtime.equivalent` warns on every run that the numbers may differ from Pro's. Each refusal that is kept
now says what that page omits, so the next look starts from the gap and not from a slogan. **Against:** an equivalent
is not Pro's computation. A footprint regularized by Douglas–Peucker and orientation snapping is not the one Esri's
compression algorithm gives, and a reader comparing outputs cell by cell will find differences.

### Alternative B — keep the blanket reasons

**For:** nothing built means nothing to be wrong. **Against:** the reasons were not true for 20 of the 33 names. The
owner asked for every capability arcpy has, so a toolbox that calls ObserverPoints, a cartogram or SKATER would stop on
a name that a cited method can answer.

### Alternative C — build everything, including the five cartography procedures and the four statistics tools refused below

**For:** the surface would be complete. **Against:** for those names, the page leaves out the part that decides the
answer: where a merged centreline goes, what makes a road detail, MaxEnt's regularization, and the operators of a
genetic algorithm. An answer built on a guess at that part is a different tool under the same name, which ADR-223 §5.2
forbids.

## 3. Counterarguments to the preferred option

*An equivalent invites false confidence.* A user who sees `RegularizeBuildingFootprint` succeed may assume Pro's
footprints. The answers to this are the warning on every first run, the docstrings that name the method, and the
`equivalents` column of `tools/grapy-refusals.json`, which counts these names apart. *Some INFERRED choices are
large.* Viewshed2's AGL is a first-order estimate (§5.4), and SurfaceParameters fits in projected rather than
geocentric coordinates. Both are named in the docstrings and in Q-213 rather than hidden. *Two refusals are by
parameter value rather than by name:* Viewshed2's `vertical_error > 0`, and SurfaceParameters' adaptive neighbourhood
and equatorial aspect. Refusing only the value keeps the default call working. The refusal of the value says why.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| ObserverPoints writes OBS1 … OBSn, 1 where the observer sees and 0 where not; Value is "binary-encoded" | *Using Viewshed and Observer Points for visibility* | doc.esri.com, read 2026-10-31 |
| Viewshed2 works in 3D geocentric coordinates, steps the ground path at cell size, interpolates by IDW, and visibility is d > 0 at every step; OBSERVERS regions are the bitwise OR of observer bits in ObjectID order; the table has Observer and Region | *How Geodesic Viewshed works* | doc.esri.com, read 2026-10-31 |
| Viewshed2's vertical error adds a uniform draw in [−0.6, 0.6] to d without relating it to the error given | same | same |
| SurfaceParameters fits a quadratic (least squares) or biquadratic (exact) surface over a window of up to 15 × 15, and cites Minár, Evans and Jenčo 2020 for the curvatures; the adaptive window uses DEV, with no rule given | *How Surface Parameters works* | same |
| SplineWithBarriers is minimum curvature (Briggs 1974) by a multigrid of Terzopoulos 1988 | *How Spline with Barriers works* | same |
| ContourWithBarriers' values-file format, its Type values 1/2/3, and that contours are made independently on each side of a barrier | tool page | same |
| The regularization methods, STATUS 0/1, tolerance types, the 0.05–0.25 precision range, and the group field of the adjacent form | the two tool pages | same |
| StackProfile's current and legacy table fields | tool page | same |
| TopoToRaster is ANUDEM 5.3, described in words only, with two different counts of contour points per cell | *How Topo to Raster works* | same |
| Hand-worked tests pass: R3 ridge visibility and AGL (14.5, 19.0) reproduced geodesically; a ring-fill of 5 over a sink of 0; the slope of z = 2x + y (65.905°, aspect 243.435°); the mean, Gaussian, Casorati and profile curvature of z = 0.01(x − 25)²; a spline flat on each side of a barrier; contours missing exactly where a barrier cuts; a chamfered footprint keeping its 45° edge; Kåsa's circle of radius 10; two adjacent footprints sharing a 10-unit wall with no overlap; a profile sampled at cell edges | `src/grapy/tests/test_surfaces3d.py`, `test_carto_lookagain.py`, `test_stats_lookagain.py` | this branch |

## 5. Decision

### 5.1 Built as equivalents (19) and built (1)

| Name | Method | Citation |
|---|---|---|
| sa/ddd.ObserverPoints | Viewshed's R3 line of sight per observer, with Value the OR of observer bits | Franklin and Ray 1994; How Geodesic Viewshed works |
| sa/ddd.Viewshed2 | the geodesic sight line of How Geodesic Viewshed works | that page; Bowring 1976 |
| sa.ZonalFill | each zone filled to the lowest weight on its boundary | Jenson and Domingue 1988 |
| sa/ddd.SurfaceParameters | quadratic or biquadratic least-squares fit; curvatures of Minár et al. | Evans 1980; Wood 1996; Zevenbergen and Thorne 1987; Minár, Evans and Jenčo 2020 |
| sa/ddd.SplineWithBarriers | minimum curvature solved directly; barriers cut the stencil | Briggs 1974; Terzopoulos 1988 |
| sa/ddd.ContourWithBarriers | How contouring works, per side of the barriers | Esri's How contouring works |
| ddd.RegularizeBuildingFootprint | Douglas–Peucker, then snapping to the dominant orientation; Kåsa circle | Douglas and Peucker 1973; Duchêne et al. 2003; Kåsa 1976 |
| ddd.RegularizeAdjacentBuildingFootprint | the same on shared arcs, with shared vertices placed once | same |
| cartography.GenerateContiguousCartogram | FLOW-BASED and DIFFUSION, both as the page names them | Gastner, Seguy and More 2018; Gastner and Newman 2004 |
| cartography.CreateCartographicPartitions | point-region quadtree | Samet 1984 |
| cartography.StripMapIndexFeatures | pages walked along the line (placement INFERRED) | the page's fields |
| stats.SpatiallyConstrainedMultivariateClustering | SKATER, with membership probability from random spanning trees | Assunção et al. 2006; Caliński and Harabasz 1974 |
| stats.LocalBivariateRelationships | entropy over a power-weighted MST with a permutation test, typed by AICc | Guo 2010; Hero et al. 2002; Hurvich and Tsai 1989; Benjamini and Hochberg 1995 |
| stats.PrepareData | random and nearest-neighbour splits; the published balancing methods | Tomek 1976; Chawla et al. 2002; Park and Jun 2009 |
| ddd.StackProfile (built, not an equivalent) | sampling at the vertices and at cell or triangle edges; the documented fields | tool page |

Every random draw comes from `env.randomGenerator` through `stats._seed()`, so a seeded call repeats.

### 5.2 Still refused, with each name's own reason

| Name | Reason |
|---|---|
| sa/ddd.TopoToRaster | ~~it is ANUDEM 5.3: drainage enforcement through the lowest saddle of each spurious sink, streams and ridges taken from contour curvature, lakes, cliffs and coastlines, over a coarse-to-fine iteration. Its page gives these steps only in words, with no roughness penalty, tolerance test, convergence rule or curvature threshold, and it gives two different counts of contour points per cell. Hutchinson's papers describe ANUDEM but do not specify version 5.3 |~~ **superseded at merge, 2026-10-31: built as an equivalent by [ADR-242](ADR-242-grapy-builds-topo-to-raster-segment-mean-shift-and-the-combinatorial-tools.md), written in parallel, which took Hutchinson 1989, Hutchinson et al. 2009 and 2011 and Barnes et al. 2014 as the specification this refusal said was missing; the two agents read the same pages and disagreed, and the build was kept because it honours every parameter the page lists and its tests hold**
| sa/ddd.TopoToRasterByFile | ~~the same, and this form reads its inputs from a parameter file for that same method |~~ **superseded at merge, 2026-10-31: built as an equivalent by [ADR-242](ADR-242-grapy-builds-topo-to-raster-segment-mean-shift-and-the-combinatorial-tools.md), written in parallel, which took Hutchinson 1989, Hutchinson et al. 2009 and 2011 and Barnes et al. 2014 as the specification this refusal said was missing; the two agents read the same pages and disagreed, and the build was kept because it honours every parameter the page lists and its tests hold**
| cartography.CollapseHydroPolygon | its pages say what it decides but not how, and they name no centreline method. What it decides: where a polygon narrower than the collapse width collapses, how a section within the width tolerance is judged by its surroundings, when the minimum length overrides the width, and how a partly collapsed polygon tapers |
| cartography.CollapseRoadDetail | its page names no way to find a road detail (what makes a configuration "open", how its diameter is measured) and no way to build the connection that replaces it |
| cartography.MergeDividedRoads | "How Merge Divided Roads works" does not quantify "generally parallel" and does not say where the merged centreline is placed. It also gives the value 1 for two different meanings of MDR_TYPE |
| cartography.ThinRoadNetwork | "How Thin Road Network works" does not say how a segment's significance in long journeys is measured or how the minimum length is applied to a segment. It gives the aim, not a method |
| cartography.GenerateHachuresForDefinedSlopes | its defaults are undefined. NOT_CONNECTED "derives" new connections between the upper and lower lines by no stated rule, and NOT_PERPENDICULAR orients hachures "to achieve even spacing" by no stated rule |
| stats.AttributeUncertainty | it reruns the analysis that made its input. It identifies that analysis (tool and parameters) from the geoprocessing history ArcGIS Pro writes into the result layer's metadata, and grapy's outputs carry none. Its pages also leave unstated the spread a margin of error gives at a confidence level, the bounds of its uniform and triangular draws, and the fields it writes |
| stats.CrossValidate | it refits the model behind a Forest-based and Boosted, Generalized Linear Regression or Presence-only Prediction result with that run's own parameters, which grapy's result features do not record. Its page names neither the fields nor the metrics it writes |
| stats.PresenceOnlyPrediction | its MaxEnt fit is named but not specified. Its pages give neither the regularization penalty nor its values, nor the hinge function's linear segment. They do not say how background points are sampled or how the entropy term H of its link functions is found |
| stats.BuildBalancedZones | its genetic growth algorithm leaves open which neighbour a growing zone adds next, and how crossover combines two parents (shown only as a diagram). It also does not say how equal area or compactness is measured |

### 5.3 Refused by parameter value

The default call of each tool below runs. Only the value named is refused, and the refusal says why.

- **Viewshed2.** `vertical_error` above 0: the page's random draw is not related to the error, and the number of
  sight lines is not given.
- **SurfaceParameters.** `ADAPTIVE_NEIGHBORHOOD`: DEV is named, but no rule for the window is given.
  `EQUATORIAL_ASPECT`: the reference direction is undefined. Geographic rasters: the fit is planar.
- **ContourWithBarriers.** `POLYGONS`: the page does not say how a polygon is closed at a barrier.
- **StackProfile.** Multipatch, terrain and LAS targets: the page does not say how the line is cut into segments.
  `out_graph`: it is a Pro chart.
- **SpatiallyConstrainedMultivariateClustering.** `GET_SPATIAL_WEIGHTS_FROM_FILE`: the .swm format is unpublished.
- **PrepareData.** `distance_features` and `explanatory_rasters`: the fields they write are not named.

### 5.4 INFERRED choices (for confirmation in Q-213) — **Confirmed 2026-10-31 by owner decision in Q-213** (*"Hepsini onayla"*)

**(a) ObserverPoints.**
- Value is the bitwise OR of 2^(i−1) over the observers that see the cell, in ObjectID order. This is Viewshed2's
  documented encoding; ObserverPoints' own page only says "binary-encoded".
- An unseen cell is 0 and has a row in the table.
- Each vertex of a line is an observer.

**(b) Viewshed2.**
- Refraction lifts the sight line by k·t(1 − t)·L²/(2R), with R = 6,370,000 m. This is How Viewshed works'
  correction, rewritten for a chord.
- Surface values are ellipsoidal heights in metres.
- PERIMETER_SIGHTLINES gives ALL_SIGHTLINES' answer.
- The number of steps is the line's length in cells, rounded up.
- IDW uses the four surrounding cell centres, weighted 1/d.
- The AGL is the largest −d/t over the steps.
- The relationship table has one row per pair of a region and an observer that sees it.
- A start angle above the end angle wraps through north.
- The device parameter is accepted, and the tool runs on the CPU.

**(c) ZonalFill.**
- A zone is every cell of one value.
- Its boundary is its cells that have an 8-neighbour in another zone, in NoData or off the raster.
- A zone with no boundary weight is NoData.

**(d) SurfaceParameters.**
- The fit is planar in projected coordinates.
- True north is found by projecting a step north from each cell.
- A cell whose window is incomplete is fitted to the cells it has, or is NoData.
- A flat cell's aspect is −1, and its slope-line curvatures are 0.
- Mean curvature is −((1 + q²)r − 2pqs + (1 + p²)t) / (2(1 + p² + q²)^1.5). Casorati curvature is √(2H² − K).

**(e) SplineWithBarriers.**
- The minimum-curvature surface is solved directly.
- A point ties the cell it falls in.
- A barrier cuts a link when the barrier crosses the segment between the two cell centres.
- A part of the grid that no point reaches is NoData.
- At a smoothing factor of 1 the data weight is 10⁻⁹.

**(f) ContourWithBarriers.**
- The field is `Contour`.
- Precedence: explicit, then indexed, then contour.
- A values file replaces the level parameters.

**(g) RegularizeBuildingFootprint and RegularizeAdjacentBuildingFootprint.**
- Defaults the pages omit: densification none, precision 0.25, diagonal penalty 1.5, minimum radius 0.1, maximum
  radius 1,000,000, angular limit 5°.
- `precision` rounds to a grid of precision × tolerance.
- A result fails, and the original is kept with STATUS 1, when it is invalid or lies more than 2 × tolerance from the
  original.
- The dominant orientation is the length-weighted circular mean of the edge directions modulo 90°.
- Each edge is turned about its midpoint.
- Parallel neighbouring edges are joined by a right-angled step.
- The criteria for choosing a diagonal and for ANY_ANGLE are those in the docstrings.
- A circle has 64 vertices.
- In the adjacent form, a null group value is a group of its own.

**(h) StackProfile.**
- Samples fall at the line's vertices and at cell or triangle edges.
- Samples with no data are left out.
- SRC_NAME is the target's base name (the full path in the legacy table).
- LINE_TARG is "<LINE_ID> <SRC_NAME>".
- SRC_TYPE is "Surface".

**(i) The cartography INFERRED choices** are listed in the docstrings of `_cartomore.py`:
- the cartogram's grid, blur, stopping rule and final scaling;
- the partition counting point and maximum depth;
- the strip map's page placement, start end, Angle convention and neighbour fields.

**(j) The statistics INFERRED choices** are listed in the docstrings of `_statsmore.py`:
- SKATER's edge cost, cut rule, random spanning trees and PROB;
- LBR's entropy estimator, field names and AICc;
- PrepareData's split sizes, SMOTE k, Tomek links and thinning.

## 6. Consequences

**Positive.**
- 20 of the 33 names run.
- The 13 that remain refused say what their pages leave out, so the next look can check exactly that.
- `tools/grapy-refusals.json` counts the 19 equivalents apart.

**Negative.**
- 19 more names answer differently from Pro by construction. The cost is a reader who compares outputs.
- Viewshed2 loops in NumPy over every cell and step. It is slow on large rasters.
- SurfaceParameters' planar fit diverges from Pro's geocentric fit at small scales.
- `_surfaces3d.py`, `_cartomore.py` and `_statsmore.py` add about 3,000 lines that only their own tests exercise.

**State.** None. These tools write only the rasters, feature classes and tables their callers name. ObserverPoints'
attribute table goes into the raster's own .aux.xml, as every grapy RAT does. Nothing goes into the catalogue, and no
runtime state is held.

**Ports created.** None. No new dependency: numpy, scipy, shapely and pyproj were already in grapy-env and in the
packages CI's grapy job installs.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A cited, published method that does what the page describes is an honest build under arcpy's name (ADR-224 §5.6) | as ADR-224 |

## 8. Dependencies

**Depends on:**
- [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.6, equivalents;
- [ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) §5.2, one-to-one names;
- [ADR-229](ADR-229-grapy-holds-las-datasets-and-tins.md), the TIN surfaces StackProfile reads.

**Depended on by:** none yet.

## 9. Revisit triggers

- Esri publishes Viewshed2's vertical-error procedure, the DEV rule of the adaptive neighbourhood, or the compression
  algorithm behind the footprint tools in a form specific enough to reproduce.
- A toolbox depends on an INFERRED choice of §5.4 answering differently from Pro.
- ANUDEM's algorithm is published as a specification.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The owner answers [Q-213](../open-questions.md).** **DISCHARGED 2026-10-31** — confirmed as built by owner decision in Q-213 (*"Hepsini onayla"*).
2. **ObserverPoints' Value encoding compared with ArcGIS Pro.** One run on two observers settles whether Pro numbers
   the combinations as bits (§5.4 a). Pro is needed; until then, the reading of §5.4 a stands.
