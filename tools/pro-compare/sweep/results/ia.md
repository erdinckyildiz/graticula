# arcpy.ia (the names not also in arcpy.sa) against ArcGIS Pro 3.7 — 2026-11-08

grapy builds 289 names of `arcpy.ia` (`tools/grapy_surface.py`'s `_defined()` on `ia.py`, intersected with
`tools/arcpy-surface.json`). 206 of them are also built `arcpy.sa` names: the same function, compared under sa
(`cases/sa_1.py`, `sa_2.py`) and not run again here; whether Pro's ia twin of an sa name ever behaves differently was
not tested, except Median (sa.CellStatistics MEDIAN probed, same as ia.Median). The other **83** are this file's, one case each in `cases/ia.py`:

AnalyzeChangesUsingCCDC, AnalyzeChangesUsingLandTrendr, ApplyComplexDataFilter, ApplyCoregistration,
ApplyGeometricTerrainCorrection, ApplyOrbitCorrection, ApplyRadiometricCalibration, ApplyRadiometricTerrainFlattening,
Buffered, Classify, ClassifyObjectsUsingDeepLearning, ClassifyPixelsUsingDeepLearning, ClassifyRaster,
ComputeAccuracyForObjectDetection, ComputeAccuracyForPixelClassification, ComputeChange, ComputeChangeRaster,
ComputeCoherence, ComputeSARIndices, ConvertSARUnits, ConvertVideoMetadata, Deburst, Despeckle,
DetectBrightOceanObjects, DetectChangeUsingChangeAnalysis, DetectChangeUsingChangeAnalysisRaster,
DetectChangeUsingDeepLearning, DetectDarkOceanAreas, DetectObjectsUsingDeepLearning, ExtractFeaturesUsingAIModels,
ExtractOceanWinds, FindArgumentStatistics, GenerateInterferogram, GenerateMaskedSARRaster,
GenerateMultitemporalCoherence, GenerateRadiometricTerrainCorrectedData, GenerateTrend, GenerateTrendRaster,
InterpolateFromSpatiotemporalPoints, MLClassify, Majority, Max, Mean, Median, Mensuration, Merge, Min, Minority,
MultidimensionalPrincipalComponents, MultidimensionalRasterCorrelation, Multilook, NonMaximumSuppression, Pansharpen,
Percentile, PixelBlock, PixelBlockCollection, PredictUsingTrend, PredictUsingTrendRaster, RadarCalibration, Range,
RasterCollection, RasterToXarray, RegionGrow, RemoveThermalNoise, S1RadiometricCalibration, S1ThermalNoiseRemoval, SR,
StdDev, SubsetBands, Sum, SummarizeCategoricalRaster, TrainDeepLearningModel, TrainIsoClusterClassifier,
TrainKNearestNeighborClassifier, TrainMaximumLikelihoodClassifier, TrainSupportVectorMachineClassifier,
TrainUsingAutoDL, TransformImageShapes, TranslatePixelsUsingDeepLearning, TrendToRGB, UnwrapPhase, Variety,
XarrayToRaster.

**First run: MATCH 14, DIFF 15, REFUSED 4, PRO-ERROR 11, BOTH-ERROR 39. Now: MATCH 32, DIFF 18, BOTH-ERROR 33.**

*First run* is the first comparison that held the case (48 names on the first full run; the not-comparable cases,
ConvertVideoMetadata and Mensuration on the second; the sixteen GRD SAR cases on the last). Most first-run errors
were the case's: Pro saves change, trend, argument and RGB rasters only as CRF (a GeoTIFF is "Unspecified error"),
grapy multidimensional ones only as netCDF (the four first-run REFUSED were that save, not a grapy refusal); Pro's
SubsetBands takes one string, not a list; PixelBlockCollection takes Raster objects; Iso Cluster needed a skip factor
of 2; Pansharpen works in Pro on integer imagery only; SummarizeCategoricalRaster runs in Pro on a CRF with an
attribute table, not on the netCDF (an input step that differs by side, as ADR-236's CRF copy for CCDC); the accuracy
tool takes at most 16 bits. Each was corrected in the case.

Every fix is pinned in `src/grapy/tests/test_pro_sweep_ia.py` or, where an existing test pinned the old answer, in
that test with a comment saying Pro was measured (`test_multidimensional.py`, `test_deep_learning.py`,
`test_rasterfns_adr245.py`, `test_video.py`, `test_lookagain.py`).

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| AnalyzeChangesUsingCCDC | PRO-ERROR | MATCH | — | case: Detect Change output saved as CRF on Pro's side. Latest and largest change match on all 400 cells. |
| AnalyzeChangesUsingLandTrendr | PRO-ERROR | DIFF | recorded | EQUIVALENT (ADR-236): 132 of 400 cells differ for the largest change, 162 for the latest (other vertex years); the segmentation's fitting is grapy's own. |
| ApplyComplexDataFilter | BOTH-ERROR | BOTH-ERROR | not comparable | needs a Sentinel-1 SLC product; none can be had here without an account (ADR-237 c3). |
| ApplyCoregistration | BOTH-ERROR | BOTH-ERROR | not comparable | as ApplyComplexDataFilter (an SLC pair). |
| ApplyGeometricTerrainCorrection | BOTH-ERROR | BOTH-ERROR | not run | GRD-capable; not run here (whole 1.4 GB scene per tool, disk C full). Compared under ADR-237 c3 on 2026-10-31 (geometry D-328). |
| ApplyOrbitCorrection | BOTH-ERROR | BOTH-ERROR | not comparable | needs a precise orbit file from Copernicus (network and an account). |
| ApplyRadiometricCalibration | BOTH-ERROR | BOTH-ERROR | not run | as ApplyGeometricTerrainCorrection; ADR-237 c3 matched sigma nought to 0.0001 dB. |
| ApplyRadiometricTerrainFlattening | BOTH-ERROR | BOTH-ERROR | not run | GRD-capable, whole-scene; not run. |
| Buffered | MATCH | MATCH | — | |
| Classify | DIFF | DIFF | recorded | the maximum likelihood .ecd: 2 of 3600 cells (ADR-243 c2 measured 7 on its image). |
| ClassifyObjectsUsingDeepLearning | BOTH-ERROR | BOTH-ERROR | not comparable | needs a model and a deep learning framework; arcgispro-py3 here has no PyTorch or TensorFlow. |
| ClassifyPixelsUsingDeepLearning | BOTH-ERROR | BOTH-ERROR | not comparable | as above. |
| ClassifyRaster | DIFF | DIFF | recorded | as Classify, 2 cells. |
| ComputeAccuracyForObjectDetection | DIFF | DIFF | fixed (partly) | Pro names the first field by min_iou (Min_IoU_is_50_percent) and puts All_Classes first, and with no class field writes All_Classes alone; grapy wrote Class and a last All row. Per-class rows match. Open: All_Classes AP — Pro 0.6875 where the classes' are 0.5 and 1 (grapy 0.75, their mean); the rule is not found. |
| ComputeAccuracyForPixelClassification | PRO-ERROR | MATCH | fixed | case: an 8-bit class raster. Pro writes 0, not null, where a row has no measure, and no IoU row; grapy wrote nulls and an IoU row (INFERRED). |
| ComputeChange | MATCH | MATCH | fixed | the categorical encoding below applies to the function too (it lists every class it encodes). DIFFERENCE and RELATIVE_DIFFERENCE matched first. |
| ComputeChangeRaster | DIFF | MATCH | fixed | CATEGORICAL_DIFFERENCE: Pro writes Value (ALL: the pair's place in the from × to grid; CHANGED_PIXELS_ONLY: the place among differing pairs, then No Change; UNCHANGED_PIXELS_ONLY: the class's place, then Changed; Other for classes not chosen), Classvalue (the class, or largest class + 1 + the place among differing pairs), "from->to" names; the tool's default filter is CHANGED_PIXELS_ONLY. grapy numbered present pairs from 1 (INFERRED, ADR-236 §5.7), default ALL. Measured on three filters and a subset. |
| ComputeCoherence | BOTH-ERROR | BOTH-ERROR | not comparable | SLC pair. |
| ComputeSARIndices | BOTH-ERROR | BOTH-ERROR | not run | GRD-capable, whole-scene; not run. |
| ConvertSARUnits | BOTH-ERROR | BOTH-ERROR | not run | as above. |
| ConvertVideoMetadata | PRO-ERROR | DIFF | fixed (partly) | Pro: latitude and longitude units are XY, DD, DDM or DMS ("Degrees" is ERROR 000800); its date-time keywords (YYYY-MM-DDTHH:MM:SS.SSS, DD/MM/YYYY hh:MM:SS.SSS AM/PM Z … 24 of them); targets named "Precision Time Stamp", "Platform Pitch/Roll Angle (Full)"; a date-time becomes a MISB time stamp counting leap seconds (+27 s in 2024); only matched fields are written; the mapping file is MISB Tag (10000 + tag), LDS Name, Local metadata heading, Units, a row per field matched. All built. Open: Pro writes an altitude given in Feet unconverted (1000) where the page requires metres — not copied. |
| Deburst | BOTH-ERROR | BOTH-ERROR | not comparable | SLC. |
| Despeckle | BOTH-ERROR | BOTH-ERROR | not run | GRD-capable, whole-scene; not run. |
| DetectBrightOceanObjects | BOTH-ERROR | BOTH-ERROR | not run | as above (and the scene is over land). |
| DetectChangeUsingChangeAnalysis | MATCH | MATCH | — | |
| DetectChangeUsingChangeAnalysisRaster | PRO-ERROR | MATCH | — | case: CRF on Pro's side. Number of changes and earliest change match on all cells. |
| DetectChangeUsingDeepLearning | BOTH-ERROR | BOTH-ERROR | not comparable | no framework, no model. |
| DetectDarkOceanAreas | BOTH-ERROR | BOTH-ERROR | not run | GRD-capable, whole-scene; not run. |
| DetectObjectsUsingDeepLearning | BOTH-ERROR | BOTH-ERROR | not comparable | no framework, no model. |
| ExtractFeaturesUsingAIModels | BOTH-ERROR | BOTH-ERROR | not comparable | Living Atlas models (portal sign-in, network) and no framework. |
| ExtractOceanWinds | BOTH-ERROR | BOTH-ERROR | not run | GRD-capable, whole-scene; not run. |
| FindArgumentStatistics | BOTH-ERROR | MATCH | aligned | case: CRF/netCDF outputs and the multiple_occurrence argument. Pro writes a time as its OLE Automation date (2001-02-15 is 36937); grapy wrote a decimal year (ADR-236 §5.3, INFERRED). |
| GenerateInterferogram | BOTH-ERROR | BOTH-ERROR | not comparable | SLC pair. |
| GenerateMaskedSARRaster | BOTH-ERROR | BOTH-ERROR | not run | GRD-capable, whole-scene; not run. |
| GenerateMultitemporalCoherence | BOTH-ERROR | BOTH-ERROR | not comparable | coherence of SLC pairs. |
| GenerateRadiometricTerrainCorrectedData | BOTH-ERROR | BOTH-ERROR | not run | GRD-capable, whole-scene; not run. |
| GenerateTrend | REFUSED | MATCH | — | first run: the case saved a multidimensional raster as GeoTIFF. Linear and harmonic match. |
| GenerateTrendRaster | REFUSED | MATCH | — | as GenerateTrend; linear, harmonic and polynomial match (p-value to 4 decimals). |
| InterpolateFromSpatiotemporalPoints | PRO-ERROR | DIFF | fixed (partly) | Pro's arcpy takes the page's six arguments and answers a Raster (a seventh, out_raster, is a TypeError); grapy took out_raster second and answered a Result. Pro's grid: top-left cell centred on the points' corner, round(extent/cell)+1 cells (13 × 13); grapy 12 × 12 from the corner. Now every cell matches for NEAREST_NEIGHBOR, IDW and MEAN. Open: Pro's slice time is a range clipped to the points (2024-05-01T12:00 … 23:59:59.914), grapy the period's start; Pro F32, grapy F64. |
| MLClassify | DIFF | DIFF | fixed (partly) | 74 cells differed: grapy wrote .gsg numbers with six decimals (a covariance of 4.06e-05 kept two digits) where Pro writes seven significant digits, and grapy could not read Pro's .gsg at all ("#  Class ID" with two spaces). Both fixed (shared with CreateSignatures, EditSignatures, ClassProbability); 4 cells still differ (ties at class borders). |
| Majority | MATCH | MATCH | — | |
| Max | MATCH | MATCH | — | |
| Mean | MATCH | MATCH | — | |
| Median | DIFF | MATCH | fixed | integer inputs: Pro answers the lower middle value as an integer (1, 2, 5, 7 → 2, U8), through ia.Median and sa.CellStatistics MEDIAN alike (probed); grapy answered 3.5 as F64. Floating point keeps the mean of the two. |
| Mensuration | PRO-ERROR | DIFF | fixed (partly) | case: points inside the image (Pro fails a distance to the edge). Pro takes (column, row) as the RPC's sample and line (point((50, 50)) is the offsets exactly); grapy read them half a pixel off. Open: Pro's area of four unclosed corners is a triangle (298,975 m²; closed, 597,966) — its page's sample passes four corners for the image size, so not copied; Pro's azimuth along a parallel is 90.0 where the WGS 84 geodesic's is 89.997. |
| Merge | DIFF | DIFF | recorded | Pro defect, not copied: MEAN is a running pairwise mean in list order (1, 2, 4, 8 → 5.375; three rasters ((a+b)/2+c)/2), where the page says "the average of the overlapping pixel values"; for integers Pro keeps S32. FIRST, LAST, MIN, MAX, SUM match. |
| Min | MATCH | MATCH | — | |
| Minority | MATCH | MATCH | — | |
| MultidimensionalPrincipalComponents | DIFF | MATCH | aligned | eigenvalues equal to 1e-12. Pro's components are x·v uncentred, loadings v·√λ, loadings fields DimValue (Date) and PC_k, eigenvalue table PC, Eigenvalue, CumVarPctg with only the components kept; grapy centred, wrote unit vectors under Slice and every eigenvalue with Percent/Cumulative (INFERRED). |
| MultidimensionalRasterCorrelation | DIFF | MATCH | fixed | a pixel NoData in any slice is NoData in Pro; grapy left the pair out (INFERRED). |
| Multilook | BOTH-ERROR | BOTH-ERROR | not run | GRD-capable, whole-scene; not run. |
| NonMaximumSuppression | MATCH | MATCH | — | |
| Pansharpen | PRO-ERROR | DIFF | fixed (partly) | Pro refuses 32-bit float for all but Gram-Schmidt ("Not implemented"); case now 16-bit. Pro keeps the integer pixel type; Esri and Simple Mean resample nearest and truncate (match every cell); Gram-Schmidt bilinear and rounds (840 of 57,600 off by one). grapy resampled bilinearly into F32. Open: Brovey (Pro's cells about a quarter of the band) and IHS do not follow the page's formulas; not found. |
| Percentile | DIFF | MATCH | fixed | NEAREST: Pro takes 0-based position (n−1)·p/100 rounded half down (1, 4, 10 at 75 → 4), measured at p = 0…100 on 2–6 values; grapy took ⌈p·n/100⌉. Pro defect, not copied: ia.Percentile ignores the interpolation type (LINEAR on integers answers NEAREST, NEAREST on floats answers LINEAR); sa.CellStatistics PERCENTILE honours LINEAR. |
| PixelBlock | PRO-ERROR | MATCH | — | case: Raster objects, [i, j] indexing. |
| PixelBlockCollection | BOTH-ERROR | MATCH | fixed | Pro keeps an edge block at full size, padded with the pixel type's lowest value (−3.4e38 F32, −2147483648 S32); grapy cut it short (INFERRED). |
| PredictUsingTrend | REFUSED | MATCH | — | first run: the case's save. |
| PredictUsingTrendRaster | REFUSED | MATCH | — | as above. |
| RadarCalibration | BOTH-ERROR | BOTH-ERROR | not run | raster function on a SAR product; not run. |
| Range | MATCH | MATCH | — | |
| RasterCollection | MATCH | MATCH | — | |
| RasterToXarray | BOTH-ERROR | DIFF | fixed (partly) | Pro's Dataset is (lat, lon, StdTime) with lat and lon at each cell's lower-left corner, lat ascending; grapy wrote (StdTime, y, x) at centres. Open, not copied: Pro's rows run north to south under lat labels that ascend (the first value is the north-west cell's) and it leaves NoData as −9999 unmarked; grapy keeps rows consistent with lat and NoData NaN. Pro refuses a single-band raster (TypeError); grapy refuses non-multidimensional ones. |
| RegionGrow | DIFF | MATCH | fixed | Pro measures similarity from the seed's value (not the region's running mean) and takes both bounds strictly; grapy used a mean and ≤. Eight-connected confirmed. Open: on a checkerboard Pro also took two cells 28.3 m away at radius 25, on the west side only — rule not found. |
| RemoveThermalNoise | BOTH-ERROR | BOTH-ERROR | not run | compared under ADR-237 c3 (look-up tables to 6e-8; floor 1e-5 aligned then). |
| S1RadiometricCalibration | BOTH-ERROR | BOTH-ERROR | not run | raster function on a SAR product; not run. |
| S1ThermalNoiseRemoval | BOTH-ERROR | BOTH-ERROR | not run | as above. |
| SR | MATCH | MATCH | — | |
| StdDev | MATCH | MATCH | — | |
| SubsetBands | BOTH-ERROR | MATCH | — | case: bands as one string ("1 3 5", "2-4 7"). |
| Sum | MATCH | MATCH | — | |
| SummarizeCategoricalRaster | PRO-ERROR | DIFF | fixed (partly) | field names: Pro C_<value> and a NoData count; grapy Class_<value> (INFERRED). Open: Pro wrote the header with no rows (three slices of a CRF with an attribute table) — not copied. |
| TrainDeepLearningModel | BOTH-ERROR | BOTH-ERROR | not comparable | no framework. |
| TrainIsoClusterClassifier | PRO-ERROR | DIFF | recorded | case: skip factor 2. EQUIVALENT (isodata): different partitions (no label mapping fits); Pro's isodata parameters are not published. |
| TrainKNearestNeighborClassifier | DIFF | DIFF | recorded | ~510 of 3600 cells (507 and 514 on two runs; which side varies was not checked); Pro favours class 3; no scaling, k or weighting tried (raw, standardised, min-max; k 1/3/5; distance-weighted) reproduces it. |
| TrainMaximumLikelihoodClassifier | DIFF | DIFF | recorded | 2 cells (ADR-243 c2). |
| TrainSupportVectorMachineClassifier | DIFF | DIFF | recorded | 302 cells: Pro's RBF with cross-validated C and γ against grapy's linear SVM (D-337). |
| TrainUsingAutoDL | BOTH-ERROR | BOTH-ERROR | not comparable | no framework. |
| TransformImageShapes | BOTH-ERROR | BOTH-ERROR | not comparable | needs pixel-space detections from Detect Objects Using Deep Learning (no framework) over a Frame Camera mosaic dataset. |
| TranslatePixelsUsingDeepLearning | BOTH-ERROR | BOTH-ERROR | not comparable | no framework, no model. |
| TrendToRGB | BOTH-ERROR | DIFF | fixed (partly) | case: CRF on Pro's side. LINEAR: Pro writes Increase_Trend, Intercept (the line's value at the series' first time) and Decrease_Trend unstretched in floating point, then RMSE, R2, P_Value; grapy wrote three 0–255 bands. Now matches. Open: HARMONIC — Pro's three 8-bit bands (215–255, 215–255, 32–158 here) follow a rule the page does not give. |
| UnwrapPhase | BOTH-ERROR | BOTH-ERROR | not comparable | SLC pair. |
| Variety | DIFF | MATCH | fixed | Pro answers Variety as an integer for floating-point inputs (U8); grapy F64. In sa.CellStatistics too. |
| XarrayToRaster | BOTH-ERROR | DIFF | fixed | reads Pro's corner-labelled lat/lon back to the same extent. Open: pixel type only (Pro F32, grapy F64 — sa_1's multidimensional debt). |

## Fixes that reach beyond these names

- **sa.CellStatistics** MEDIAN of integers (lower middle, integer) and VARIETY (always integer): the code ia.Median and
  ia.Variety run; MEDIAN probed through sa.CellStatistics in Pro.
- **Signature files** (`_signatures.write_gsg`, `_classify._read_gsg`): CreateSignatures, EditSignatures,
  ClassProbability and MLClassify write and read seven significant digits and Pro's own layout.
- **ia.ComputeChange** shares ComputeChangeRaster's categorical encoding.
- **sa.PrincipalComponents** (sa_2's) was not touched: MultidimensionalPrincipalComponents' docstring said it computes
  as sa's does; whether Pro's sa.PrincipalComponents is also uncentred is for sa_2 to measure.
- Not touched, per the coordinator: `_horn` (Slope, Aspect) and `_edge_mask` (Hillshade).

## Open differences worth a debt row

1. The sixteen Sentinel-1 GRD tools and raster functions were not run (whole-scene runs, disk C full); only three were
   compared, under ADR-237 c3. A window-based Pro comparison of the GRD chain would cover them: ~1 day.
2. Pansharpen Brovey and IHS: Pro's cells are not the page's formulas; measure Pro on one-band cases: ~half a day.
3. TrendToRGB HARMONIC's colour rule; ComputeAccuracyForObjectDetection's All_Classes AP; Mensuration's azimuth
   (rhumb, not geodesic?); RegionGrow's one-sided radius on a checkerboard — each small once measured.
4. Pro defects, not copied: Merge MEAN as a running pairwise mean; ia.Percentile ignoring its interpolation type;
   ConvertVideoMetadata leaving Feet unconverted; Mensuration's area dropping the last of four corners;
   RasterToXarray's north-first rows under ascending lat and unmarked −9999; SummarizeCategoricalRaster's empty table.
5. Multidimensional slice time: InterpolateFromSpatiotemporalPoints' slices are a time range in Pro (with
   AggregateMultidimensionalRaster's, sa_1 item 2), and F32 against grapy's F64 (sa_1 item 1).
6. EQUIVALENT classifiers: Iso Cluster's partition, kNN (~510 cells), SVM (D-337),
   LandTrendr's vertices (132–162 of 400 cells).
