# ADR-233 — grapy holds geostatistical layers

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the kriging systems, the semivariogram models' formulas and the cross-validation statistics, each checked in closed form or by hand · `MEDIUM` for the layer as a model of what Geostatistical Analyst holds · `LOW` for agreement with ArcGIS Pro's numbers wherever a parameter is fitted — the pages say "weighted least squares" and not which weights, lags or optimiser — and for the K-Bessel and stable models' scaling, §5.9 |
| **Decided** | 2026-10-30, by owner decision: *"Yap"* — grapy gets geostatistical layers, as the group-2 data models before it did ([ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.7). The layer's form and the rules the pages leave open are INFERRED and listed for confirmation in §5.9 and [Q-201](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Before this decision `arcpy.ga` built seven names — `IDW` and `GlobalPolynomialInterpolation` to a raster, and the five
search neighbourhood classes — and refused nineteen with one reason: *"it reads or writes a geostatistical layer, a
model object grapy does not have."* `arcpy.CrossValidationResult` and `arcpy.GeostatisticalDatasets` were refused with
the same reason. Kriging existed only as `arcpy.sa.Kriging`, an equivalent that writes a raster and keeps no model.

Most of Geostatistical Analyst passes a *geostatistical layer* from tool to tool: a toolbox makes one, cross-validates
it, changes a parameter, makes it again on other data, and exports it to a raster or to points. Without the object
none of that chain runs.

What ArcGIS publishes, and what it does not (pages read on doc.esri.com and pro.arcgis.com on 2026-10-30, by fetching
and quoting them; no Esri implementation was read, CLAUDE.md §5):

- **The tools' signatures, parameters and keywords are documented**, and several pages document results precisely:
  GA Layer To Points names its output fields (`Source_ID`, `Included`, `Predicted`, `Error` = predicted minus the
  validation value, `StdError`, `Stdd_Error`, `NormValue`); the CrossValidationResult page names its statistics; GA
  Layer To Grid defines block averaging as "the mean of all predictions made within that cell"; Compare Geostatistical
  Layers gives its criteria, scores, tolerance rule, tie rule (1, 2, 2, 4) and exclusions; GA Neighborhood Selection
  says a sector short of `nbrMin` takes "the nearest available point outside it"; Densify Sampling Network gives its
  four criteria as formulas.
- **The layer's own form is not published.** It is an ArcGIS layer file or a model XML; GA Set Model Parameter shows
  XPath strings into that XML (`/model[@name = 'Kriging']/model[@name = 'Variogram']/value[@name = 'Nugget']`) but not
  the document.
- **The fitting is named, not specified.** The semivariogram is fitted by "weighted least squares"; the default lag,
  the number of lags, the weights and the optimiser are not given. IDW's optimal power, RBF's small-scale parameter
  and LPI's bandwidth are "optimised" by cross-validation searches that are not given.
- **The semivariogram models are standard.** Johnston et al. (2001), *Using ArcGIS Geostatistical Analyst*, Appendix A
  — Esri's own published book — and the textbook literature (Cressie 1993; Isaaks and Srivastava 1989; Chilès and
  Delfiner 2012; Matheron 1962) give spherical, circular, tetraspherical, pentaspherical, exponential, Gaussian,
  rational quadratic, stable and K-Bessel (Matérn). The page that lists Pro's formulas was not reachable on 2026-10-30
  (404), so the forms are the literature's, and where the literature leaves a constant open the choice is INFERRED.
- **Empirical Bayesian kriging is described, not specified**: semivariograms simulated at random from subsets, with
  the subset, simulation and transformation choices not given.

## 2. Alternatives considered

### Alternative A — grapy's own layer as documented JSON, models from the literature (chosen)

A geostatistical layer is a JSON document: method, parameters as fitted or given, neighbourhood, and a copy of the
points it was fitted to. A model with no points is what ArcGIS keeps as model XML. Kriging, cokriging and the
semivariogram models are the textbook ones; what a page names without specifying is an equivalent (ADR-224 §5.6),
marked and counted apart. **For:** every number is checkable on paper; no Esri format is reproduced; the whole chain
— make, cross-validate, change a parameter, remake, export — runs. **Against:** a layer file or model XML made in Pro
cannot be read, and fitted parameters will differ from Pro's.

### Alternative B — read and write Esri's layer files and model XML

**For:** interchange with Pro, and the XPath strings would address real documents. **Against:** the form is
unpublished; reproducing it means reconstructing Esri's document from fragments, which the clean room forbids
(CLAUDE.md §5). Rejected.

### Alternative C — adopt a geostatistics library (PyKrige, gstools, scikit-gstat)

**For:** fitted, tested kriging. **Against:** the computation is small — a linear system per prediction — and the
libraries' own conventions (lag binning, model scaling, fit weights) would become grapy's answers without being any
closer to Pro's; one more wheel in a fenced image buys nothing the tests cannot already check. Rejected; numpy and
scipy, already in the image, suffice.

### Alternative D — keep the layer refused

Rejected by the owner's *"Yap"*.

## 3. Counterarguments to the preferred option

**Fitted kriging will not match Pro's.** Two implementations of "weighted least squares" with different lags, weights
and starting points give different ranges and sills, and so different surfaces. A user comparing grapy's kriging with
Pro's on the same data will see differences in the second or third significant figure, more on awkward data. The answer
is that the user can hold every parameter (GASetModelParameter, or the JSON), and then grapy's kriging is the textbook
system solved exactly; the fit is marked an equivalent and says so in the tool's messages.

**A layer copies its data.** Pro's layer references its dataset; grapy's holds the points. A toolbox that edits the
source and expects the layer to follow will not see the edit until it runs GACreateGeostatisticalLayer again. That is
also what makes a fitted model answer the same tomorrow, and the copy is a few numbers per point.

**XPath strings without an XML.** GA Get/Set Model Parameter take Esri's XPaths. grapy answers exactly the paths the
pages print, against its own model, and refuses others by name — a toolbox using an undocumented path fails loudly
rather than silently writing nowhere.

## 4. Evidence

The tests (`src/grapy/tests/test_geostat.py`, sixteen; `test_ga.py` updated) check against closed forms and hand
solutions, not against grapy's own output read back:

| Claim | Evidence | Source |
|---|---|---|
| The semivariogram models are their formulas | values at t = 0.5 and 1; stable α = 1, 2 is the exponential, Gaussian; K-Bessel ν = ½ gives 0.95 at the range and 1 − √0.05 at half of it | `test_semivariogram_models_answer_their_formulas` |
| The fit is Cressie's WLS | z = 0, 1, 0, 1 on a line: γ̂ = 0.5, 0, 0.5 with 3, 2, 1 pairs; with the sill held, the cost's optimum is nugget 0.3 exactly | `test_the_empirical_semivariogram_and_its_weighted_least_squares_fit` |
| Ordinary kriging | two points: 5 at the midpoint, variance 2γ(30) − γ(60)/2; the datum and zero error at a data point, with and without a nugget | `test_ordinary_kriging_at_the_midpoint_of_two_points` |
| Simple kriging | one point, mean 4: 4 + 6e⁻¹, variance 2 − 2e⁻² | `test_simple_kriging_from_one_point_and_a_known_mean` |
| Universal kriging | data on z = 2x + 3y + 1 reproduced at three places | `test_universal_kriging_reproduces_a_plane` |
| Cokriging | with no cross-covariance, equal to ordinary kriging of the primary in value and error | `test_cokriging_with_no_cross_covariance_is_ordinary_kriging` |
| Cross-validation | IDW power 1 on three points: errors 13.333, 0, −13.333, RMSE √(2·(40/3)²/3) | `test_cross_validation_by_hand` |
| Block averaging | x² at block centres 2.5 and 7.5: 31.25 | `test_block_averaging_takes_the_mean_of_each_cells_predictions` |
| LPI and RBF | a plane reproduced; order 0 with the constant kernel the mean 53/3; every basis exact at the points | `test_local_polynomial_and_radial_basis_functions` |

The full grapy suite runs 597 tests, exit status 0. No performance claim is made: a prediction solves a system the size
of its neighbourhood, a user tool runs under ADR-223's limits, and a grid too large for them fails by those limits.

## 5. Decision

1. **grapy holds geostatistical layers**, made by the interpolators and by GACreateGeostatisticalLayer, read by every
   tool that reads one.
2. **The layer is grapy's JSON (INFERRED, §5.9 (a))**, `"format": "grapy-geostatistical-layer"`, version 1: method
   (`IDW`, `GLOBAL_POLYNOMIAL`, `LOCAL_POLYNOMIAL`, `RADIAL_BASIS`, `KRIGING`, `COKRIGING`), parameters, neighbourhood
   (an ellipse with `nbrMax` and `nbrMin`, or null for every point), spatial reference, extent, and the datasets — each
   its path, field and a copy of its points. A layer with no datasets is a *model*. A name with no folder and no
   extension is a layer held for the process, as grapy's feature layers are; a path is a file. A file that is not this
   JSON — a `.lyrx`, a model XML — is refused by name, the format being unpublished. `_geostat.py`'s docstring is the
   format's documentation.
3. **Kriging is ordinary, simple and universal** (linear or quadratic drift), in γ form for ordinary and universal and
   covariance form for simple (Cressie 1993, §3.2–3.4), the standard error being the square root of the kriging
   variance. **Ordinary cokriging** of one secondary variable, in covariance form with weights summing to 1 and 0
   (Isaaks and Srivastava 1989, ch. 17), every point of both datasets used. Indicator, probability and disjunctive
   kriging, transformations, declustering and measurement error are refused by name: their thresholds,
   back-transformations, weights and error split are not given.
4. **The semivariogram** is one model plus a nugget, γ(0) = 0: spherical, circular, tetraspherical, pentaspherical,
   exponential, Gaussian, rational quadratic, stable and K-Bessel, with geometric anisotropy (a minor range and a
   direction). A parameter not given, or marked `@auto`, is **fitted by Cressie's (1985) weighted least squares** —
   Σ N(h)·(γ̂(h)/γ(h) − 1)² over lags (k−1)L < h ≤ kL — by Nelder–Mead, as `arcpy.sa.Kriging` already fits; the stable
   and K-Bessel shape is fitted when not given. **J-Bessel and hole effect are refused**: they have no practical range,
   and the scaling between their range and their argument is not given. Fitting an anisotropic range is refused (how
   directional semivariograms combine in the fit is not given); with the ranges and direction held, sill and nugget
   are fitted on the rescaled distances.
5. **Cokriging's three models are given, not fitted**: how the cross-semivariogram is fitted with the direct ones and
   kept positive definite is not given.
6. **Built to their pages:** IDW and global polynomial interpolation as layers and rasters; GACreateGeostatisticalLayer;
   GeostatisticalDatasets; GA Get/Set Model Parameter for the paths the page prints (a kriging layer's Nugget, Range,
   Sill, NumberOfLags and their `@auto`); GACalculateZValue; GA Layer To Grid with block averaging; GA Layer To Rasters
   (prediction, standard error, and the normal quantile and exceedance probability kriging's own assumption gives);
   GA Layer To Points with the documented fields; GA Layer To Contour (lines, MANUAL and EQUAL_INTERVAL);
   CrossValidation and CrossValidationResult; CompareGeostatisticalLayers; GA Neighborhood Selection. The documented
   `nbrMin` rule is applied by every neighbourhood, which lifts IDW's earlier refusal of a cell with too few points
   inside its circle.
7. **Built as equivalents (ADR-224 §5.6)**, marked and counted apart: GACreateGeostatisticalLayer (its fit),
   LocalPolynomialInterpolation (kernel-weighted local least squares, Fan and Gijbels 1996, with the compact CONSTANT,
   EPANECHNIKOV and QUARTIC kernels — EXPONENTIAL, GAUSSIAN and POLYNOMIAL5 refused, their shape against the bandwidth
   not given), RadialBasisFunctions (exact RBF interpolation, Franke 1982, Hardy 1971, Mitášová and Mitáš 1993), and
   GAMovingWindowKriging (the model refitted in each window, Haas 1990).
8. **Refused, each for its own reason:** empirical Bayesian kriging, EBK 3D and EBK regression prediction (EBK is
   described, not specified; ordinary kriging is not EBK); the two 3D layer exports (only EBK 3D makes a 3D layer);
   GASemivariogramSensitivity, GaussianGeostatisticalSimulations, SubsetFeatures and CreateSpatiallyBalancedPoints
   (drawn at random); DensifySamplingNetwork (whether the network is updated after each choice, and where it looks
   without candidates, are not given); ExploratoryInterpolation (its candidates are default and optimized parameter
   sets that are not given, and include EBK); ArealInterpolationLayerToPolygons (areal kriging's fitting is not given);
   and those refused before for reasons unchanged — the barrier interpolators, IDW3D and NearestNeighbor3D,
   GenerateSubsetPolygons, ExtractValuesToTable, DirectionalTrend. **None is refused for a schedule.**
9. **INFERRED, for confirmation** ([Q-201](../open-questions.md)):
   - (a) the layer is grapy's JSON of §5.2, holding a copy of its points; a bare name lives for the process, a path is
     a file;
   - (b) angles — a neighbourhood's and an anisotropy's direction — are the major axis' direction in degrees clockwise
     from north; nearness within an ellipse is the distance scaled to it; ties keep the data's order;
   - (c) the semivariogram's lags cover half the largest distance between points, 12 by default; the fit starts from
     half the largest lag, the largest semivariance and no nugget; a parameter set by GASetModelParameter is held,
     `@auto` true is refitted; Sill in the printed XPath is the partial sill;
   - (d) the K-Bessel argument is scaled so the range holds 95 % of the sill, and the stable model is 1 − exp(−3tᵅ),
     both by the convention of the exponential and Gaussian models' factor 3; the circular, tetraspherical and
     pentaspherical forms are the literature's, Pro's formula page being unreachable;
   - (e) kriging is exact — the nugget is all micro-scale variation, none measurement error; simple kriging's mean
     defaults to the data's; universal kriging's semivariogram is fitted to the residuals of a least-squares trend;
   - (f) block averaging predicts at the centres of an even subdivision of the cell; standard errors and the
     quantities from them are refused for blocks larger than one point;
   - (g) cross-validation holds the model as fitted, writes `Measured` beside the documented fields, numbers
     `Source_ID` by the point's position, and puts NormValue at the plotting positions (i − 0.5)/n;
   - (h) GA Layer To Contour draws DRAFT on the layer's default grid and splits EQUAL_INTERVAL over the predictions'
     range on it; the value field is `Contour`;
   - (i) CompareGeostatisticalLayers' table fields beyond RANK and Included, its weighted average Σwᵢrᵢ/Σwᵢ, the next
     SORTING block starting at the best of the rest, and the sample standard deviation in MIN_PERC_ERROR;
   - (j) RBF: the thin-plate spline carries a linear trend and the others a constant; LPI's bandwidth limits the
     neighbourhood further; GAMovingWindowKriging's output fields and its lag rule per window.

## 6. Consequences

- **State.** In the layer files a tool writes in its run directory, or in the process. Nothing in the catalogue or the
  server; no new wheel (numpy and scipy).
- **Names.** `tools/grapy-refusals.json` before → after: `arcpy.ga` built 7 → 23 (equivalents 0 → 4), refused
  35 → 19; `arcpy` built 99 → 101, refused 84 → 82 (`CrossValidationResult`, `GeostatisticalDatasets`); none
  unexamined.
- **IDW answers where it refused.** A cell whose circle holds fewer than `nbrMin` points now takes the nearest outside,
  the rule GA Neighborhood Selection documents; `test_ga.py` checks the hand answer where it once checked the refusal.
- **A Pro layer or model XML fails by name**, and the toolbox must make the layer in grapy — one GACreateGeostatisticalLayer
  from a JSON model, whose fields §5.2 and `_geostat.py` document.

## 7. Assumptions this decision rests on

- A user tool's interpolation fits in its run: a prediction solves a dense system of its neighbourhood (or of every
  point, for a model without one), O(n³) per place. Nothing has been measured; a grid too large fails by ADR-223's
  limits.
- scipy's Nelder–Mead, Bessel and exponential-integral functions are correct to the tests' tolerances.

## 8. Dependencies

[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.6–5.7,
[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) (the fence and limits), `arcpy.sa.Kriging` (whose fit this
generalises), numpy, scipy; clean room (CLAUDE.md §5): only documented behaviour and the literature, no Esri file format.

## 9. Revisit triggers

- Pro's semivariogram formula page becomes reachable: §5.9 (d) is checked against it and corrected or confirmed.
- A comparison with Pro on one dataset (condition 4) shows held-parameter kriging differing beyond rounding — the
  system would then not be the textbook one, and the cause is found before anything else is built on it.
- Esri publishes empirical Bayesian kriging's subset and simulation rules, or the layer's XML schema.

## 10. Dissent

None recorded.

## 11. Conditions

1. **Kriging, cokriging and the semivariogram models answer hand-worked values.** **DISCHARGED 2026-10-30**:
   `test_geostat.py` — the models at t = 0.5 and 1, the WLS optimum, ordinary, simple and universal kriging and
   cokriging in closed form (§4).
2. **The layer's chain runs**: an interpolator or GACreateGeostatisticalLayer makes a layer, GA Get/Set Model
   Parameter change it, it is made again on the same data with the held value kept, and it is exported to a grid,
   rasters, points and contours. **DISCHARGED 2026-10-30**: `test_a_model_fitted_to_data_and_its_parameters_by_path`,
   `test_idw_as_a_layer_and_its_grid`, `test_rasters_points_and_contours_from_a_kriging_layer`.
3. **Every `arcpy.ga` name is built, built as an equivalent, or refused for its own reason.** **DISCHARGED
   2026-10-30**: 23 built (4 equivalents), 19 refused, none for a schedule, none unexamined.
4. **Held-parameter kriging is compared with ArcGIS Pro** on one dataset — ordinary kriging, spherical, nugget, range
   and sill held, a standard neighbourhood — using the owner's Pro install; predictions and standard errors agree to
   rounding, or the difference is found and recorded.
5. **The owner confirms or changes §5.9** ([Q-201](../open-questions.md)).
