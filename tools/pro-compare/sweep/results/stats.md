# arcpy.stats against ArcGIS Pro 3.7 — 2026-11-08

All 43 names grapy builds (38 in `stats.py`; LocalBivariateRelationships, PrepareData and
SpatiallyConstrainedMultivariateClustering from `_statsmore.py`; EvaluateBinSizes and HotSpotAnalysisComparison from
their own modules), one case each (`cases/stats.py`). The shared fixture's 30 points are too few for the regressions
and clusterings, so the case module builds `sp` (60 points with a trending `y`, two explanatory fields, a count and a
population), `sz` (a 6 × 6 grid of 100 m squares with the same fields), `ts` (three locations on ten days) and
`quarters` (four zones), from the fixtures' linear congruential sequence. env.randomGenerator is set to
`"1 MERSENNE_TWISTER"` on both sides wherever a tool draws; Pro's draws are still not grapy's, so a permutation p-value
or a random seed is expected to differ and is recorded, not fixed.

**First run: MATCH 9, DIFF 25, SCHEMA 4, PRO-ERROR 4, GRAPY-ERROR 1. Now: MATCH 18, DIFF 19, SCHEMA 4, PRO-ERROR 2.**

Two of the first run's PRO-ERRORs were the case: CalculateCompositeIndex (Pro takes the variables' reverse flag as
`true`/`false`, not `REVERSE`), DecomposeSpatialStructure (Pro takes `ALL`/`NO_FIELDS`; grapy's own default was
`APPEND_ALL`, which Pro refuses — fixed in grapy too). The two that remain are Pro's: CalculateAreas and
SpatialAssociationBetweenZones (below). The GRAPY-ERROR was grapy refusing a run without `output_table`.

Fixes are pinned in `src/grapy/tests/test_pro_sweep_stats.py` and, where an existing test pinned the old answer, in
that test in `test_stats.py`, changed with a comment saying Pro was measured (Local Moran's I's variance, Neighborhood
Summary Statistics, Export XYv, Dimension Reduction, GLR, Spatial Outlier Detection, Similarity Search, OLS, Calculate
Composite Index, Calculate Rates, Incremental Spatial Autocorrelation, GWR's field names).

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| AverageNearestNeighbor | MATCH | MATCH | — | |
| BivariateSpatialAssociation | DIFF | DIFF | recorded | Lee's L, the smoothing scalars, the local L and the neighbourhood averages equal; only the permutation p-values (and the significance and category read from them) differ — Pro's draws. |
| CalculateAreas | PRO-ERROR | PRO-ERROR | not comparable | Pro reports success and writes nothing (to a geodatabase or a shapefile; probed). The tool is gone from Pro's documentation; grapy follows ArcMap's page. |
| CalculateCompositeIndex | PRO-ERROR (case) | DIFF | fixed | Values equal to 1e-6. Aligned: the new output holds SOURCE_ID and the variables only, then `<field>_PREPROCESSED`, INDEX, INDEX_RANK and INDEX_PCTL in percent (grapy copied every field and wrote `_SCALED`, COMP_INDEX, COMP_PCTL as a fraction). The DIFF left is Pro's third output, a group layer (`…_Layers`), which a server does not make. |
| CalculateDistanceBand | MATCH | MATCH | — | |
| CalculateRates | DIFF | SCHEMA | fixed in part | Rates equal. Aligned: the output holds SOURCE_ID and the rate fields' count and population only; the k-th rate is R&lt;k&gt; followed by R&lt;k&gt;_CR (crude rate × multiplier); outputs `(out, "", "")`. Not written: R1_SD (null) and R1_XS (1.0 everywhere here) — undocumented. |
| CentralFeature | MATCH | MATCH | — | |
| ClustersOutliers | DIFF | DIFF | fixed | LMiZScore was up to 0.25 off with 0 permutations. Pro's variance is Sokal, Oden and Thomson's conditional one: b2 = (n−1)Σz⁴/(Σz²)² over all features, the B term over k ≠ h and added — grapy now equals Pro to 1e-10 on 60 points (grapy read equation (9) as printed). Added NNeighbors, ZTransform ((x − x̄)/σ, population σ) and SpatialLag. Left: the derived output Pro answers as `OBJECTID` (the input's ObjectID field name) where grapy answers `SOURCE_ID`. |
| CollectEvents | MATCH | MATCH | — | |
| ColocationAnalysis | DIFF | DIFF | recorded | Pro writes only the features of the category of interest (31 of 60), with SOURCE_ID, LCLQ, PVALUE, LCLQBIN, LCLQTYPE, NEIGHCATS, NEIGHPREV; its LCLQ differs from grapy's literature form (0.620 against 0.734 at feature 2) and its bins are coded differently (2 for "Isolated - Significant"). Esri's formula page answers 404; a rework is a day's probing. Outputs now `(out, "")` as Pro's. |
| DecomposeSpatialStructure | PRO-ERROR (case) | DIFF | fixed in part | grapy now takes ALL/NO_FIELDS (Pro refuses APPEND_ALL). Different: Pro names the components C1… and adds SOURCE_ID, and its eigenvectors differ (0.355 against 0.286 at feature 1 for C1) — Esri's method page answers 404; recorded. |
| DensityBasedClustering | SCHEMA | MATCH | fixed | Clusters equal. Pro writes SOURCE_ID, CLUSTER_ID and COLOR_ID only (grapy kept the input's fields). COLOR_ID equalled CLUSTER_ID for all six clusters measured (INFERRED beyond that). |
| DimensionReduction | DIFF | MATCH | aligned | Pro scales by the population standard deviation (grapy: n − 1, an INFERRED note now measured), signs a component so the record with its largest-magnitude score is positive, writes SOURCE_ID and PCA1, PCA2 …, and answers four outputs. Scores equal to 1e-14. |
| DirectionalDistribution | MATCH | MATCH | — | |
| DirectionalMean | SCHEMA | SCHEMA | recorded | Values equal. Pro also writes the Rayleigh test (TestStat 8.06, RefValue 5.99, PValue 0.023, UnifTest); 8.06 is not 2nR̄² (7.51) and 0.023 is not its χ² tail — the statistic is not documented (grapy's docstring already says so). |
| EvaluateBinSizes | DIFF | DIFF | recorded | Pro recommends 137.0 m, grapy 109.3 m; Pro's chart table is BINSIZE, UNIT, UNIFORMITY, COUNTVAR, EVALUATION (the same 100 sizes) and its bins LOCATION and JOIN_COUNT (27 bins against 36). The scores differ from the first size on (count variety 0.147 against 0.119) — Pro's simulated bins are random, by its page. |
| ExportXYv | DIFF | MATCH | aligned | Pro writes x and y with eight decimal places and floats with six (`500170.37600000`, `1.314800`); grapy wrote eight and six significant digits (INFERRED, now measured). |
| GWR | DIFF | DIFF | fixed in part | Coefficients equal. Aligned: σ² = RSS/(n − 2 tr S + tr S'S) (standard errors and STDRESID now equal Pro's to 1e-10; grapy took n − tr S); Pro's fields and order — SOURCE_ID, INTRCPT/C_&lt;VAR&gt;, SE_, T_, SG_ (1 where |T| exceeds t at α = .05/(tr S/k); measured threshold between 2.616 and 2.692, which df n − k fits), COOKS_D, CND_NUMBER; outputs `(out, "", "")`. Left: Pro's LOCALR2 (0.952 against 0.937) and CND_NUMBER (189 against 3.5) — formulas not documented. |
| GeneralizedLinearRegression | DIFF | MATCH | fixed | Pro writes SOURCE_ID first and STDRESID last (residual / √(RSS/(n−k)) for CONTINUOUS) and answers `(out, "", "")`; grapy answered the coefficients (now only in the messages). STDRESID for BINARY/COUNT is INFERRED (Pearson). |
| GeographicallyWeightedRegression | DIFF | DIFF | fixed in part | ADAPTIVE uses the bisquare kernel: Pro's coefficients equal stats.GWR's bisquare at 25 neighbours (grapy used the Gaussian). Left: the legacy tool's own schema (Observed, Cond, LocalR2, Predicted, Intercept, C1_x1 …, StdError, StdErr_Intercept …, StdResid, Source_ID — grapy writes stats.GWR's) and its StdResid (−0.0989 against −0.0975), whose per-feature StdError is not documented. |
| HighLowClustering | MATCH | MATCH | — | The case uses `cnt` (General G needs values ≥ 0). |
| HotSpotAnalysisComparison | DIFF | DIFF | recorded | Built on two HotSpots outputs (which now carry NNeighbors). Pro's similarity 0.55 against 0.549, expected similarity, kappa and the category labels ("Cold to Cold" against "Cold 95% - Cold 95%") differ; permutation-based, and the labelling is not documented. |
| HotSpots | DIFF | DIFF | fixed | Gi* equal. Added NNeighbors (the features with a weight other than zero, itself included). Left: the derived output `OBJECTID` against `SOURCE_ID`, as for ClustersOutliers. |
| IncrementalSpatialAutocorrelation | DIFF | MATCH | fixed | At each distance Pro leaves out the features with no neighbour (three of 60 at 80 m: E[I] = −1/56); the table's fields are z_score and p_value. |
| LocalBivariateRelationships | DIFF | DIFF | recorded | Pro's ENTROPY 1.52 against grapy's 0.85 at feature 1 — grapy's equivalent uses Guo's power-weighted MST estimator; Pro's estimator is not documented. |
| MGWR | DIFF | DIFF | recorded | Pro writes the scaled fit (S_Y, S_X1 …, S_C_, S_SE_, S_T_, S_SG_) and the unscaled coefficients, and its golden search lands on other bandwidths (C_X2 −0.370 against −0.477). grapy's equivalent omits standard errors (documented). Days of work. |
| MeanCenter | MATCH | MATCH | — | |
| MedianCenter | DIFF | DIFF | recorded | 1.2 m apart on 60 points. Kuhn–Kuenne's iteration: Pro's start or stopping rule is not documented. |
| MultiDistanceSpatialClustering | DIFF | MATCH | fixed | L(d) equal; Pro answers `(table, "")`. |
| MultivariateClustering | DIFF | DIFF | recorded | k-means++ seeds are random: Pro's partition is not grapy's. Pro also writes SOURCE_ID and IS_SEED. |
| NeighborhoodSummaryStatistics | SCHEMA | MATCH | fixed | Pro's STD and SKWNS weigh by the raw weights (√7 and 7^(−1/6) times grapy's normalised forms over seven values); equal weights take linear quartiles (Hyndman–Fan 7) for MED, IQR and QIMBL; fields SOURCE_ID, &lt;FIELD&gt;_Mean, _STD, _SKWNS, _MED, _IQR, _QIMBL, _NNBRS and DIST_…. Kernel-weighted quartiles keep grapy's weighted quantile (not measured). |
| OptimizedHotSpotAnalysis | DIFF | DIFF | recorded | Pro's scale of analysis is narrower (12 neighbours at feature 1 against grapy's 27): the incremental search's start and step are grapy's own (documented in the docstring). Outputs now `(out, "")`. |
| OptimizedOutlierAnalysis | DIFF | DIFF | recorded | As OptimizedHotSpotAnalysis (11 against 26 neighbours), and the permutation p-values. Outputs now `(out,)`. |
| OrdinaryLeastSquares | DIFF | DIFF | fixed in part | Aligned: Variable in capitals (`X1`) and StdCoef (β·sd(x)/sd(y)). Left: Pro's Diag table carries a Definition text field (Esri's prose, not copied), and Prob differs by 3e-6 (0.3451377 against 0.3451410) — the t distribution's tail as Pro computes it. |
| PrepareData | SCHEMA | SCHEMA | recorded | With splitting NONE and the default append, Pro writes SOURCE_ID and the variables only; grapy keeps every field (its default is APPEND, as the page lists). Not measured with other options. |
| SAR | DIFF | DIFF | recorded | Pro's fit differs (PREDICTED 2.106 against 1.281 at feature 1, NUM_NEIGHS equal); which model AUTO chose is in Pro's messages, not compared. Pro also names STDRESID, LAGRESID, NUM_NEIGHS. |
| SimilaritySearch | DIFF | MATCH | fixed | Pro gives the feature to match SIMRANK 0 and SIMINDEX 0, and writes LABELRANK. |
| SpatialAssociationBetweenZones | PRO-ERROR | PRO-ERROR | not comparable | Pro fails with ERROR 000582 after ~35 s on every pair of polygon zone sets tried (three, probed), with and without an output; on two rasters it refuses the same raster twice. |
| SpatialAutocorrelation | MATCH | MATCH | — | |
| SpatialOutlierDetection | DIFF | MATCH | fixed | LOF equal. LOCAL writes SOURCE_ID, OUTLIER_ID, LOF only (grapy kept the input's fields and wrote IS_OUTLIER) and answers `(out, "")`. GLOBAL's fields were not measured. |
| SpatiallyConstrainedMultivariateClustering | GRAPY-ERROR | DIFF | fixed | grapy refused a run without output_table; Pro runs without one. The partition is the same as Pro's; the cluster numbers differ (Pro's numbering is neither by size nor by lowest ObjectID), and Pro writes SOURCE_ID first. |
| StandardDistance | MATCH | MATCH | — | |
| TimeSeriesSmoothing | SCHEMA | SCHEMA | recorded | Smoothed values equal. Pro names V_SMTH, V_NBR (a double), LOCATIONID (from 0) and HTML_CHART, keeps SOURCE_ID and the time and value fields, and drops the input's other fields; grapy writes SMOOTH_v, NUM_NBRS, TIME_WINDOW and keeps every field. Renaming is small but the HTML chart is not built; left for the owner. Outputs now `(out, "")`. |

## Open differences worth a debt row

- **Local statistics schemas** — several Pro outputs keep only SOURCE_ID and the analysis fields (ColocationAnalysis,
  TimeSeriesSmoothing, PrepareData, MultivariateClustering's IS_SEED, DecomposeSpatialStructure's C1…), where grapy
  keeps or names fields its own way. Each is an hour; ColocationAnalysis also differs in its LCLQ and bins (a day).
- **GWR's LOCALR2 and CND_NUMBER**, the legacy GWR's schema and StdError, and **MGWR**'s scaled outputs and bandwidths
  — formulas not documented; MGWR's standard errors are days.
- **HotSpots / ClustersOutliers derived output**: Pro answers the input's ObjectID field name (`OBJECTID`); grapy
  answers `SOURCE_ID`.
- **Equivalents whose undocumented choices differ**: OptimizedHotSpot/OutlierAnalysis (scale of analysis),
  LocalBivariateRelationships (entropy estimator), EvaluateBinSizes (recommended size), MedianCenter (iteration), SAR,
  DirectionalMean's Rayleigh test, CalculateRates' R_SD/R_XS, OLS's Prob in the 6th digit.
- **Not comparable here**: CalculateAreas (Pro writes nothing) and SpatialAssociationBetweenZones (Pro fails with
  000582 on every input tried).
