# ADR-243 — grapy builds the image classifiers, pansharpening, segmentation and Fu and Rich's solar radiation as cited equivalents

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-31, by owner decision on scope (round two of ADR-224's sweep: *"go on until everything is done, then go back to the start and look at the rest once more"*): the `arcpy.ia` and `arcpy.sa` names refused as *"its algorithm is named or described but not specified closely enough to reproduce"* and as *"Fu and Rich's (2000) model is given only in part"* are re-read against their tool pages, raster function pages and *How … works* topics. Each is built, built as a cited equivalent, or refused with a reason of its own. The choices the pages leave open are INFERRED and listed in §5.4 and [Q-212](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.6 allows an *equivalent*: where Esri's page names
or describes an algorithm without specifying it closely enough to reproduce, grapy may build a published method that
does what the page describes, cite it, mark it with `_runtime.equivalent`, and say so when it runs. Round one of the
sweep gave about forty `arcpy.ia` and `arcpy.sa` names a one-line refusal — *"named or described but not specified
closely enough"* — and nine solar radiation names and four time classes a longer one: the direct and diffuse formulas
are text, but the sun map and sky map are discretised *"in ways defined only in Fu 2000 (a thesis)"*.

Round two read the pages again. Two things were wrong with the round-one reasons.

- **The methods are published.** Maximum likelihood classification is Richards and Jia's; ISO cluster is isodata (Ball
  and Hall 1965); the support vector machine is Cortes and Vapnik's; k-nearest neighbour is Cover and Hart's;
  Gram-Schmidt pansharpening is Laben and Brower's patent (2000); mean shift is Comaniciu and Meer (2002). Each tool
  page names its method and gives its parameters; what it does not give is Esri's exact code, which ADR-224 §5.6 does
  not require.
- **The solar model is described, and its sources are citable.** *How solar radiation is calculated* gives equations
  1–9 as text. *Modeling solar radiation* describes the discretisation in words that are enough to build it: horizon
  angles traced in `calculation_directions` and interpolated between them, a hemispherical raster of `sky_size` cells a
  side with radius proportional to zenith angle, a sun map of sectors by day interval and hour interval, a sky map of
  zenith and azimuth divisions, and each sector's gap fraction as unobstructed cells over all its cells. A thesis (Fu
  2000) and a 1994 paper (Rich et al.) are published and citable. Neither is needed to reproduce the description.

## 2. Alternatives considered

### Alternative A — build each named method as a cited equivalent that honours the page's parameters; refuse only where the output itself is undefined (chosen)

**Argument for.** A toolbox that classifies an image, pansharpens a scene or maps insolation stops on a refusal that
names nothing missing except Esri's code. Each equivalent takes the page's parameters, defaults and outputs. Each
cites its method. Each warns once per run that its numbers may differ from Pro's.

**Argument against.** A maximum likelihood classification and an insolation map will not match Pro's numbers cell for
cell. The sun map's sector boundaries, the hemispherical projection's orientation and the classifier file are grapy's.

### Alternative B — keep the round-one refusals

**Argument for.** Nothing is claimed that is not Esri's.

**Argument against.** The reason is false as worded: the method is named and published, and the page gives what the
user controls. ADR-224 §5.6 exists for this case.

### Alternative C — evaluate the sun continuously and ignore `sky_size` and the divisions

**Argument for.** Simpler, and closer to the physics.

**Argument against.** It leaves four parameters unhonoured. A user who sets `sky_size` or the divisions expects them to
change the answer as the page says. Round one refused for this reason. Alternative A honours them instead.

## 3. Counterarguments to the preferred option

- *An equivalent that cannot match Pro is a trap.* It warns on every run, it is counted apart on the status page, and
  §11 carries the comparison with Pro as a condition.
- *grapy's .ecd is not Pro's.* That is true. Train → ClassifyRaster round-trips inside grapy. A Pro-written .ecd is
  refused by name, not misread. INFERRED and listed in Q-212.
- *The random trees stay refused while the other classifiers are built.* The page names a randomised procedure it does
  not define. A random forest needs a decision-tree learner, and none is among grapy's libraries. scikit-learn was not
  added, by instruction.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Direct radiation on a flat plane follows equations 2–4 | One sector at solar noon on day 172 at 40° N gives 636.2 Wh/m², as computed by hand from Spencer's declination | `test_solar_radiation.Formula` |
| Equation 3's elevation term is applied | At 1,000 m the path shortens by exp(−0.118 − 0.0016) | same |
| A day's sectors sum, partial at sunrise and sunset | Agrees with an independent sum to 1e-6 | same |
| The diffuse weights sum to 1 and the open-sky share is ½ | Equations 7 and 8, and ∫cos θ sin θ dθ | same |
| A south-facing slope receives more than flat, and flat more than north-facing | 45° N over the special days | `test_solar_radiation.Terrain` |
| A pit is shaded, and a wall's horizon is atan(h/d) | — | same |
| Interval bands sum to the total, in Area and Raster tools | — | `test_solar_radiation.Intervals`, `.RasterTool` |
| Points agree with Area at a cell centre | — | `test_solar_radiation.Points` |
| Raster Solar Radiation follows the formula each half hour with NOAA's sun | — | `test_solar_radiation.RasterTool` |
| The classifiers, pansharpening, segmentation and surface tools meet their papers' worked cases | — | `test_classify.py`, `test_surface_tools.py` |

## 5. Decision

### 5.1 Built as equivalents (`arcpy.ia` and `arcpy.sa` unless marked)

| Name | Method and source |
|---|---|
| TrainMaximumLikelihoodClassifier | Gaussian maximum likelihood, equal priors (Richards and Jia 2006, §8.3) |
| TrainIsoClusterClassifier | isodata (Ball and Hall 1965; Tou and Gonzalez 1974) |
| TrainKNearestNeighborClassifier | k-nearest neighbour plurality vote (Cover and Hart 1967) |
| TrainSupportVectorMachineClassifier | one-against-one linear soft-margin SVM by SMO (Cortes and Vapnik 1995; Platt 1998; Hsu and Lin 2002) |
| ClassifyRaster, Classify | apply grapy's classifier definition; output carries Value, Count, ClassName |
| MLClassify | maximum likelihood with EQUAL, SAMPLE or FILE priors (FILE in Class Probability's documented a priori layout), chi-squared reject fractions and the 14-level confidence raster (Richards and Jia §8.3) |
| EditSignatures (sa) | the documented remap, merged classes taking the pooled mean and covariance of their members (Chan, Golub and LeVeque 1979) |
| ClassProbability (sa) | Bayes posteriors of the Gaussian classes (Richards and Jia §8.3), priors EQUAL, SAMPLE or FILE |
| InspectTrainingSamples | each training sample scored as the share of its cells grapy's classifier definition classes correctly |
| ComputeSegmentAttributes | segments as 4-connected cells of one colour; compactness 4πA/P² (Polsby and Popper 1991), rectangularity A over the minimum bounding rectangle (Rosin 1999) |
| Pansharpen | Esri, Brovey and Simple Mean as the pansharpening fundamentals page writes them; additive IHS (Tu et al. 2001); Gram-Schmidt (Laben and Brower 2000) |
| RegionGrow | seeded region growing (Adams and Bischof 1994) |
| SegmentMeanShift, SegMeanShift | mean shift (Comaniciu and Meer 2002) |
| SurfaceParameters (sa) | quadratic and biquadratic least-squares fit (Evans; Wood 1996; Zevenbergen and Thorne 1987), curvatures of Minár et al. 2020 |
| ObserverPoints, Viewshed2 (sa) | R3 line of sight (Franklin and Ray 1994), as sa.Viewshed |
| SplineWithBarriers (sa) | thin-plate spline (Briggs 1974) with barrier discontinuities (Terzopoulos 1988) |
| AreaSolarRadiation, PointsSolarRadiation, SolarRadiationGraphics (sa) | Fu and Rich's viewshed, sun map and sky map model, with equations 1–9 of *How solar radiation is calculated* (Fu and Rich 2002; Rich et al. 1994; Fu 2000; Spencer 1971; Kasten and Young 1989) |
| RasterSolarRadiation (sa) | the same equations each half hour, the sun placed by NOAA's Solar Calculator (Meeus 1998) |

**Built to the page:** ZonalFill (sa), CreateSignatures (sa, the signature file layout How Create Signatures works prints), and the time classes TimeWithinDay, TimeMultipleDays, TimeSpecialDays and
TimeWholeYear (ia and sa).

### 5.2 Still refused, each with its own reason

- **TrainRandomTreesClassifier, TrainRandomTreesRegressionModel (ia).** The page names a randomised procedure without
  defining it. A random forest equivalent (Breiman 2001) needs a decision-tree learner, which is not among grapy's
  libraries.
- **PredictUsingRegressionModel (ia).** It reads only the regression definition that the random trees regression
  model writes.
- **TasseledCap.** It chooses its coefficients by the sensor named in the raster's metadata and has no sensor
  parameter. grapy's rasters carry no sensor metadata.
- **ContourWithBarriers (sa).** The page does not say how cells beyond a barrier are kept out of a contour's
  interpolation. It names no field for the contour value.
- **TopoToRaster, TopoToRasterByFile (sa).** This is Hutchinson's ANUDEM. Its drainage enforcement, derived streams and
  ridges, and lake and cliff adjustments are described in words only. No published interpolator stands in for those
  constraints.
- **Dendrogram (sa).** Its VARIANCE distance between classes is given only as an image. The ASCII graph it writes is
  shown by one example, without the rules for its layout, scale bars or rounding.
- **RemoveRasterSegmentTilingArtifacts.** The page does not say how a segment cut by a tile boundary is recognised (which
  segments meeting along a tile line are rejoined), nor what value the rejoined segment takes.
- **FeatureSolarRadiation (sa).** Its output table holds each feature's total and per-area insolation, direct,
  diffuse and duration. The page names none of those fields: they appear only in a picture of an example table.
  RasterSolarRadiation computes the same model for cells.

### 5.3 The solar model as built

`_insolation` follows *Modeling solar radiation* step by step:

1. Horizon angles are traced in `calculation_directions` azimuths, one cell at a time, to the raster's edge, with
   bilinear sampling, and interpolated linearly between directions.
2. The hemispherical raster has `sky_size` cells a side, with radius proportional to zenith angle.
3. Each sun map sector covers one hour slot of one day interval. Its centroid is the sun at the middle of the slot's
   sunlit part, on the interval's middle day. Its duration is the sunlit part times the days.
4. Each sky map sector covers one ring of `zenith_divisions` and one slice of `azimuth_divisions`.
5. Each sector's gap fraction is the share of its own hemispherical cells that the viewshed leaves unobstructed.

Equations 2–9 are applied as written. Area, Points and Graphics place the sun in solar time from latitude, as their
pages say: the latitude parameter, else the raster's mean latitude, else 45°. Raster Solar Radiation places it by UTC,
longitude and latitude for each block of cells. It refuses a lunar surface by name, because NASA's SPICE kernels are not
in grapy, and it refuses `GPU_ONLY`.

### 5.4 INFERRED (listed in Q-212)

1. **Sun position (Area, Points, Graphics).** Spencer's (1971) declination; solar time; no eccentricity correction (the
   page's constant is *"at the mean earth-sun distance"*).
2. **Special days.** Days 172, 80 and 355. Within-day slots begin at sunrise when the start is earlier (the page: *"t0
   will be sun rise"*). An instantaneous configuration (start = end) gives irradiance in W/m². Multi-day hour slots are
   aligned on 0:00 solar time.
3. **Optical path beyond 80°.** The page says only that *"refraction is important"*. m(θ) is equation 3's elevation term
   times Kasten and Young's (1989) air mass.
4. **Equation 6.** The sum is weighted by each sector's duration, so `R_glb · Dur` is the period's global normal energy.
5. **Sector cells.** A sector owns the cells its track crosses, sampled every day of its interval and at nine times
   across its hours. Its gap fraction is taken over its own cells, so two tracks that overlap do not take each other's
   cells. The page warns that overlap can zero a track in Pro.
6. **Hemispherical rasters.** North up, east right. The viewshed is 1 visible, 0 obstructed, NoData outside. Sun map
   sectors are numbered from 1 in time order, with a winter-to-summer band and a summer-to-winter band. Sky map sectors
   are numbered ring by ring from the zenith, clockwise from north.
7. **Elevation and slope.** m(θ) reads the cell's z times `z_factor`, in metres. Slope and aspect come from Horn's 3 × 3
   for Area and Points.
8. **Points output.** One field `T0` without intervals, and `T0`, `T1` … with them. A location off the surface is null.
9. **Raster Solar Radiation.**
   - H3 hexagons are replaced by square blocks of the level's mean area from the page's table.
   - Horizons are traced in 32 directions.
   - The diffuse sky map is 8 × 8 on a 200-cell sky.
   - Where Surface Parameters cannot fit, Horn's slope and aspect are used.
   - Interval outputs are multidimensional rasters over `StdTime`: `global_solar_radiation`, `direct_solar_radiation`,
     `diffuse_solar_radiation` and `direct_duration`.
10. **The classifier definition.** It is grapy's own documented JSON (`"format": "grapy-ecd"`), written with the .ecd
    extension. Esri does not publish the .ecd layout.
11. **The classifiers.**
    - The .gsg layout is read from the two printed examples.
    - MLClassify's likelihood is the chi-squared tail of the Mahalanobis distance.
    - Training polygons take the cells whose centres they contain.
    - `max_samples_per_class` is drawn with seed 0.
    - ISO seeds are spaced evenly from m − s to m + s.
    - The SVM is linear with C = 1 on standardised bands.
    - A kNN tie goes to the nearest sample.
12. **Pansharpen, RegionGrow, mean shift.**
    - Pansharpen resamples bilinearly.
    - Gram-Schmidt sensor weights cover the four sensors the page lists.
    - RegionGrow is 8-connected.
    - Mean shift's radii are 21 − detail.
13. **Surface Parameters.** The fit is planar, not geocentric. The adaptive window is the largest window with
    |DEV| ≤ 1.
14. **ZonalFill.** A zone's boundary is its own cells that have a neighbour outside the zone.
15. **ObserverPoints.** Observer n contributes 2ⁿ⁻¹.
16. **Viewshed2.** It uses the curvature correction in place of the geocentric transform.
17. **SplineWithBarriers.** Its smoothing weight is (1 − s)/s.
18. **The signature tools.**
    - CreateSignatures writes six decimals, always Type 1, and no Covariance section for MEAN_ONLY. Covariance divides
      by n − 1. A text sample field numbers its classes in sorted name order.
    - EditSignatures pools statistics, so `sample_interval` changes nothing.
    - ClassProbability rounds scaled probabilities.
    - MLClassify reads FILE priors in Class Probability's layout.
19. **InspectTrainingSamples.** The score field is `accuracy`, and a later sample wins an overlap.
20. **ComputeSegmentAttributes.** Fields Value, Red/Green/Blue (Gray), Count, Compactness, Rectangularity, Mean_n and
    Std_n; segments numbered in row-major order; population standard deviation; perimeter including holes.

## 6. Consequences

**Positive.** These tools now run under their own names: a classification chain (signatures or training, then classify, inspect and probabilities), pansharpening,
segmentation, the surface parameters, the viewshed variants, and insolation for areas, points and rasters.

**Negative.**
- The answers are not Pro's numbers, and the status page counts them apart.
- An insolation run over a large DEM is slow: per cell, horizons are traced in Python-driven numpy loops. That cost,
  and the missing comparison with Pro, are [D-334](../architecture-debt.md).
- grapy's .ecd does not interoperate with Pro's.

**State.** None in the catalogue. A user tool writes its own files: rasters, feature classes, and grapy's classifier
definition (.ecd, JSON). Nothing is held between runs.

**Ports created.** None. No new dependency: numpy and scipy (sparse, ndimage, stats) were already in grapy-env.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ADR-224 §5.6: a cited equivalent that honours the page's parameters may stand for an algorithm the page does not specify | Accepted (ADR-224) |

## 8. Dependencies

**Depends on:** [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) (§5.6),
[ADR-236](ADR-236-grapy-holds-multidimensional-rasters.md) (the interval outputs),
[ADR-238](ADR-238-grapy-holds-multipatch-features.md) (`_solar`, the NOAA sun).

**Depended on by:** —

## 9. Revisit triggers

- A Pro comparison (§11) shows a systematic difference: for example, the sun map's sector centroids or the overlap rule
  of INFERRED 5.
- Esri publishes the .ecd layout.
- A decision-tree learner enters grapy's libraries, which reopens the random trees.

## 10. Dissent

The solar tools could be refused again on the grounds that Pro's sector boundaries are its own. That is rejected
because the page's parameters change the answer here as the page says they should, and the run says it is an
equivalent.

## 11. Conditions

1. **Compare with Pro.** Run AreaSolarRadiation and PointsSolarRadiation on one DEM, with one time configuration, in Pro
   and in grapy. Record the difference per cell for direct, diffuse and duration. Then follow up any systematic part of
   it.
2. **Compare the classifiers with Pro.** Use one image and one set of training samples, for maximum likelihood and the
   SVM. Record the agreement as overall accuracy against Pro's classification.
3. **Confirm Q-212**, the INFERRED choices of §5.4, with the owner.
