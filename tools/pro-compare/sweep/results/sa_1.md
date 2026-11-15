# arcpy.sa (first half: ACos … MTVI2) against ArcGIS Pro 3.7 — 2026-11-08

The first 151 names grapy builds in `arcpy.sa`, in Python's sorted order (`ACos` through `MTVI2`), one case each
(`cases/sa_1.py`), run under Pro 3.7's arcpy and under grapy. **First run: MATCH 91, DIFF 38, SCHEMA 2, REFUSED 12, PRO-ERROR 7, BOTH-ERROR 1. Now: MATCH 117, DIFF 26, SCHEMA 1, GRAPY-ERROR 2, PRO-ERROR 4, BOTH-ERROR 1.**

The *First run* column is the first comparison Pro completed. Before it, grapy could not build the case module's
eight-band image and three-band inputs at all: `NumPyArrayToRaster` refused a 3-D array and `RasterToNumPyArray` a
multiband raster (both top-level names, built here — the page documents both), so the index functions, the band
tools, ColorspaceConversion, ColormapToRGB, ClassProbability and ExportTrainingData were not reached; Lookup,
ExtractValuesToPoints (ALL) and FuzzyOverlay had been fixed too by then (they are marked *fixed* below). Seven first-run
PRO-ERRORs were the case's fault and were corrected (Arithmetic's 'Times' → 'Multiply', CutFill's workspace,
ComputeSegmentAttributes' segmented input, Foreach's function) or are recorded as not comparable.

Of the 34 non-matching names now, every one has a reason below: 5 not comparable here, the rest recorded (undocumented
algorithms grapy builds as an EQUIVALENT, Pro defects, or work estimated). Every fix is pinned by a hand-worked test
in `src/grapy/tests/test_pro_sweep_sa_1.py`; tests in `test_sa.py`, `test_sa_more.py`, `test_ia.py`, `test_ddd.py` and
`test_multidimensional.py`, `test_zm.py` that pinned the old answers were changed, each with a comment saying Pro was measured.

Classes are compared through the tool that takes them: the fuzzy functions through FuzzyMembership, the horizontal
factors through PathDistance with a horizontal raster, the kriging models through Kriging, Extent through
ExtractByRectangle.

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| ACos | MATCH | MATCH | — |  |
| ACosH | MATCH | MATCH | — |  |
| ASin | MATCH | MATCH | — |  |
| ASinH | MATCH | MATCH | — |  |
| ATan | MATCH | MATCH | — |  |
| ATan2 | MATCH | MATCH | — |  |
| ATanH | MATCH | MATCH | — |  |
| Abs | MATCH | MATCH | — |  |
| AddSurfaceInformation | REFUSED | SCHEMA | recorded | points (Z) match. Lines: grapy refuses Z_MIN … AVG_SLOPE and SURFACE_LENGTH on a raster (only Z at points is built); Pro writes the seven fields. Building them needs Pro's sampling along a line (not measured): ~1 day. |
| Aggregate | MATCH | MATCH | — |  |
| AggregateMultidimensionalRaster | DIFF | DIFF | recorded | cells match (each slice's mean and minimum). Two differences: Pro answers F32 for an F32 netCDF, grapy's multidimensional rasters are always F64; and Pro's aggregated slice carries a time *range* clipped to the data — (2000-01-15, 2000-12-31T23:59:59.914), (2001-01-01, 2001-07-15) — where grapy writes the interval's start (ADR-236 §5.3's INFERRED rule). |
| Anomaly | MATCH | DIFF | recorded | cells match on every slice; only the pixel type differs (Pro F32, grapy F64 — the multidimensional pixel-type difference). |
| ApplyEnvironment | REFUSED | MATCH | fixed | extent + cell size: Pro lays the output grid from the extent's lower-left corner with enough whole cells to cover it (100…460 m for 100…450 at 20 m) and takes the input cell holding each centre; grapy cut on the input grid first (90…450 m), up to 3.3 m off. Mask: grapy refused (ExtractByMask refused the mask environment it was applying); Pro keeps the input's whole extent. |
| AreaSolarRadiation | DIFF | DIFF | recorded | EQUIVALENT (grapy's docstring): 2969 of 3600 cells differ, max 5.5 Wh/m² (well under 1 %); the sky-map and diffuse model are not published. |
| ArgStatistics | DIFF | MATCH | aligned | Pro counts bands from 0 and answers an integer (U8); for a multidimensional raster it answers the slice index from 0, not a date. grapy answered 1-based band indexes as float and a decimal year (ADR-236 §5.3, INFERRED). FindArgumentStatistics on bands shares the code and is 0-based in Pro too (probe). |
| Arithmetic | PRO-ERROR | MATCH | fixed | the first case used operation_type 'Times' (Pro: 'Multiply' — case fixed). Pro answers Multiply of two integer rasters as F64; grapy kept integers. All operations now answer floating point (Multiply measured; the rest INFERRED). |
| Aspect | DIFF | MATCH | fixed | Pro writes NoData on the raster's outer ring and on every cell with a NoData north/south/east/west neighbour (a corner-only NoData neighbour is computed); grapy computed edges from the centre's value. Slope and Hillshade share it (Slope measured by probe: 236 ring cells) and were fixed too. |
| BAI | MATCH | MATCH | — | first run needed NumPyArrayToRaster of a 3-D array (top-level, built here). |
| BandArithmetic | REFUSED | MATCH | fixed | method 0, a user expression "(B4 - B3) / (B4 + B3) * B1", was refused; built as that arithmetic over B1…Bn. Method 4 (MSAVI) and 7 (GVITM) shared the wrong formulas below. |
| Basin | REFUSED | MATCH | — | matches once the flow direction is taken from the unfilled DEM (the filled one has flats grapy refuses). |
| BitwiseAnd | MATCH | MATCH | — |  |
| BitwiseLeftShift | MATCH | MATCH | — |  |
| BitwiseNot | MATCH | MATCH | — |  |
| BitwiseOr | MATCH | MATCH | — |  |
| BitwiseRightShift | MATCH | MATCH | — |  |
| BitwiseXOr | MATCH | MATCH | — |  |
| BlockStatistics | DIFF | MATCH | fixed | MAJORITY of an integer raster: Pro integer, grapy floating point. The integer rule Focal Statistics already had now applies to Block Statistics. |
| BooleanAnd | MATCH | MATCH | — |  |
| BooleanNot | MATCH | MATCH | — |  |
| BooleanOr | MATCH | MATCH | — |  |
| BooleanXOr | MATCH | MATCH | — |  |
| CIg | MATCH | MATCH | — |  |
| CIre | MATCH | MATCH | — |  |
| CellStatistics | DIFF | MATCH | fixed | MAJORITY (and MINORITY) with no single winner: Pro answers the lowest tied value, grapy NoData (2396 cells). |
| ClassProbability | DIFF | DIFF | recorded | EQUIVALENT: 1122 of 10800 cells differ; most by 1 (Pro looks truncated) and some by up to 100 — Pro's probability is not grapy's Bayes posterior over the signatures. Not aligned: the formula is not published. |
| ClayMinerals | MATCH | MATCH | — |  |
| ColormapToRGB | MATCH | MATCH | — | the colour-mapped input is a palette GeoTIFF written with PIL and a world file on both sides. |
| ColorspaceConversion | DIFF | DIFF | fixed (partly) | halves were rounded in floating point: hue 11.25° is 7.5 → Pro 8, grapy 7.4999… → 7; saturation 212.5 → 213. Now computed in whole numbers. The inverse (hsv_to_rgb) still differs by 1 in 4 of 10800 cells: recorded. |
| CombinatorialAnd | MATCH | MATCH | — |  |
| CombinatorialOr | MATCH | MATCH | — |  |
| CombinatorialXOr | MATCH | MATCH | — |  |
| Combine | MATCH | MATCH | — |  |
| Complex | PRO-ERROR | PRO-ERROR | not comparable | Pro cannot build a complex raster from arcpy here (NumPyArrayToRaster refuses complex64) and the sweep has no complex source. |
| CompositeBand | MATCH | MATCH | — | first run needed RasterToNumPyArray of several bands (top-level, built here). |
| ComputeSegmentAttributes | PRO-ERROR | DIFF | recorded | Pro takes only a raster it marked segmented (ERROR 001647 for a raster of segment numbers), so each side segments with its own Segment Mean Shift, which in grapy is an EQUIVALENT: the inputs differ, and so do the answers. Not comparable on a shared input. |
| Con | MATCH | MATCH | — |  |
| Contour | REFUSED | DIFF | fixed (partly) | Pro writes Id (1…n) before Contour and the highest contour first; grapy wrote Contour alone, lowest first. Ties ordered by eastmost x (fits all four ties measured; INFERRED). CONTOUR_POLYGON stays refused (ContourMin/ContourMax polygons): recorded, ~1 day. |
| ContourList | SCHEMA | MATCH | fixed | as Contour: the Id field and the order. |
| ContourWithBarriers | DIFF | DIFF | recorded | same number of lines, but Pro's lines run past the outer cell centres to the raster's edge (500599.73 …), grapy's stop at the centres; the line pieces differ. Extrapolating to the edge is undocumented. |
| Corridor | MATCH | MATCH | — |  |
| Cos | MATCH | MATCH | — |  |
| CosH | MATCH | MATCH | — |  |
| CostAllocation | MATCH | MATCH | — |  |
| CostBackLink | MATCH | MATCH | — |  |
| CostDistance | MATCH | MATCH | — |  |
| CostPath | MATCH | MATCH | — |  |
| CostPathAsPolyline | SCHEMA | MATCH | fixed | Pro writes PathCost (the accumulated cost at the destination) before DestID; grapy wrote DestID alone. |
| CreateConstantRaster | MATCH | MATCH | — |  |
| CreateNormalRaster | MATCH | MATCH | — | random: compared by extent, cell, type and mean/std rounded to 0.1 — the mean rounds to 0.1 on some grapy runs (2400 cells); not a defect. |
| CreateRandomRaster | MATCH | MATCH | — | random: compared by extent, cell, type and range. |
| CreateSignatures | DIFF | MATCH | fixed | Pro writes the class id as Class Name when no name is given; grapy left it empty. The layers' names (Pro writes the path) are left out of the comparison. |
| Curvature | MATCH | MATCH | — |  |
| CutFill | PRO-ERROR | MATCH | — | the first case failed inside Pro (ZonalStatisticsAsTable could not open the workspace); set workspace and scratch workspace to the work folder. |
| DarcyFlow | DIFF | DIFF | recorded | EQUIVALENT: Pro's residual is NoData on the outer ring (236 cells) and differs elsewhere (max 0.079); grapy's finite-difference balance is its own (DarcyVelocity matches). |
| DarcyVelocity | MATCH | MATCH | — |  |
| DeepLearningModelToEcd | BOTH-ERROR | BOTH-ERROR | not comparable | needs a pixel classification model (.dlpk) that both sides can read; Pro reads only Esri's, grapy only its own. |
| Diff | MATCH | MATCH | — |  |
| DimensionalMovingStatistics | PRO-ERROR | PRO-ERROR | not comparable | Pro takes only a multidimensional raster layer or a CRF (ERROR 110289); grapy refuses CRF and has no multidimensional layer tool. |
| DistanceAccumulation | DIFF | DIFF | recorded | EQUIVALENT (surface reconstruction, documented): 3348 of 3600 cells differ, max 13.9; the back direction too. |
| DistanceAllocation | DIFF | DIFF | recorded | allocation matches but 9 cells (ties); its distance is DistanceAccumulation's. |
| Divide | MATCH | MATCH | — |  |
| EVI | MATCH | MATCH | — |  |
| EditSignatures | DIFF | DIFF | recorded | Pro recomputes the signatures from the bands at the sample interval (class 2: 25 cells, means 0.202 …); grapy edits the file's statistics (EQUIVALENT). The sampling scheme is not documented. |
| ElevationVoidFill | DIFF | DIFF | recorded | EQUIVALENT (IDW for the page's plane-fit method): 147 cells of the hole differ, max 2.5 m. |
| EqualTo | MATCH | MATCH | — |  |
| EqualToFrequency | DIFF | DIFF | recorded | Pro defect: with a constant in in_rasters ([ints, zones, 2]) Pro counts how many of the *rasters and the value raster* equal the constant — it takes the constant as the value raster. The same call without the constant matches. Not copied. |
| EucAllocation | DIFF | MATCH | fixed | feature sources: Pro widens the sources' extent to whole multiples of the cell size (500050…500550); grapy started at the extent (500055). |
| EucDirection | DIFF | MATCH | fixed | Pro answers the bearing from each cell *to* its source, rounded to a whole degree (S16); grapy answered the bearing from the source, in floating point. Grid as EucAllocation. |
| EucDistance | REFUSED | MATCH | fixed | grid as EucAllocation; out_direction_raster was refused and now writes EucDirection's raster. |
| Exp | MATCH | MATCH | — |  |
| Exp10 | MATCH | MATCH | — |  |
| Exp2 | MATCH | MATCH | — |  |
| Expand | MATCH | MATCH | — |  |
| ExportTrainingDataForDeepLearning | DIFF | DIFF | recorded | different layouts: Pro writes 12-digit tile names with world files, every tile including the partial edge ones (4), stats.txt and esri_* files; grapy 9-digit names, full tiles only (1) and its own JSON. Aligning the names and edge tiles is small; the esri_* files are Esri's own. |
| Extent | MATCH | MATCH | — | compared through ExtractByRectangle. |
| ExtractBand | MATCH | MATCH | — |  |
| ExtractByAttributes | MATCH | MATCH | — |  |
| ExtractByCircle | MATCH | MATCH | — |  |
| ExtractByMask | DIFF | MATCH | fixed | Pro's output covers the mask features' extent (51 × 51 cells for polygons over 50…560 m), for INSIDE and OUTSIDE; grapy answered the whole input. |
| ExtractByPoints | DIFF | MATCH | fixed | a point on a cell corner: Pro takes the cell above and to the right, grapy the one below. |
| ExtractByPolygon | MATCH | MATCH | — |  |
| ExtractByRectangle | DIFF | DIFF | recorded | a rectangle edge through a row or column of cell centres: Pro keeps or drops them by a rule two measurements could not settle (columns on both edges kept; rows kept on one edge and dropped on the other, the side changing between rectangles). 30 cells. |
| ExtractMultiValuesToPoints | MATCH | MATCH | — |  |
| ExtractValuesToPoints | DIFF | MATCH | fixed | Pro writes null RASTERVALU for NoData in a geodatabase (-9999 only in a shapefile, as the page says); grapy wrote -9999. ALL on a raster with no attribute table was refused by grapy; Pro writes RASTERVALU alone. |
| FerrousMinerals | MATCH | MATCH | — |  |
| Fill | REFUSED | DIFF | recorded | the fill matches; z_limit stays refused (how it applies to nested sinks is not documented). |
| Filter | DIFF | MATCH | fixed | HIGH: Pro uses the kernel *How Filter works* gives (−0.7 corners, −1 sides, 6.8 centre) summed over the valid cells without rescaling; grapy used a Laplacian 8/−1 (EQUIVALENT, now built to the page). Under DATA a NoData centre with valid neighbours gets a value. |
| Float | MATCH | MATCH | — |  |
| FlowAccumulation | REFUSED | GRAPY-ERROR | recorded | grapy refuses a flow-direction raster with a loop; the unfilled DEM's two-cell pit is one, and Pro accumulates it (one cell of the loop takes the other's total). Undocumented: ~half a day once the rule is measured. |
| FlowDirection | REFUSED | DIFF | recorded | 1 cell of 3600 differs, beside the two-cell pit (Pro E, grapy NE — NE is steeper); out_drop_raster and FORCE on a filled DEM (flats) stay refused. |
| FlowLength | REFUSED | GRAPY-ERROR | recorded | as FlowAccumulation: grapy refuses the loop; Pro gives both pit cells length 0. |
| FocalStatistics | REFUSED | MATCH | fixed | under DATA Pro computes a NoData cell from its valid neighbours (47 cells: the hole's rim and a lone cell); grapy kept them NoData. The case's circle uses CELL units: NbrCircle in MAP units is refused by grapy (NbrCircle is the next agent's range). |
| Foreach | DIFF | DIFF | fixed (partly) | Pro reads the Arithmetic raster function's own argument names — {"Raster2": 2, "Operation": 3} multiplies each slice by 2; grapy refused 'Operation' and put the slice in Raster2. Remap matches. Pixel type F32 vs F64 remains (the multidimensional difference). Slope/Aspect/Statistics answer NoData everywhere in Pro on this netCDF (no coordinate system), so the case uses Arithmetic and Remap. |
| FuzzyGaussian | MATCH | MATCH | — |  |
| FuzzyLarge | MATCH | MATCH | — |  |
| FuzzyLinear | MATCH | MATCH | — |  |
| FuzzyMSLarge | MATCH | MATCH | — |  |
| FuzzyMSSmall | MATCH | MATCH | — |  |
| FuzzyMembership | MATCH | MATCH | — |  |
| FuzzyNear | MATCH | MATCH | — |  |
| FuzzyOverlay | MATCH | MATCH | fixed | PRODUCT, SUM and GAMMA were refused; built as *How Fuzzy Overlay works* gives them (∏μ, 1 − ∏(1 − μ), SUM^γ · PRODUCT^(1−γ)); all match Pro. |
| FuzzySmall | MATCH | MATCH | — |  |
| GEMI | MATCH | MATCH | — |  |
| GNDVI | MATCH | MATCH | — |  |
| GVITM | DIFF | MATCH | fixed | grapy weighted band 7 by −1.18; the formula (and Pro) is −0.18. BandArithmetic method 7 already had −0.18. |
| GenerateMultidimensionalAnomaly | DIFF | DIFF | recorded | magnitudes match; Pro's PERCENT_DIFFERENCE_FROM_MEAN is signed ((x − µ)/((x + µ)/2)) where its page writes /x − µ/ — not copied — and Pro names the variable temp_percent_diff_anomaly where grapy keeps temp. |
| GreaterThan | MATCH | MATCH | — |  |
| GreaterThanEqual | MATCH | MATCH | — |  |
| GreaterThanFrequency | MATCH | MATCH | — |  |
| HeatIndex | DIFF | DIFF | fixed (partly) | Pro uses the NWS's simple formula wherever its mean with T is below 80 °F; grapy applied the Rothfusz regression everywhere (1098 cells, up to 8 °F). 88 cells still differ, by up to 0.65 °F, where RH > 85 % and 80 ≤ T ≤ 87 °F: Pro adjusts there by an amount that is not the NWS's adjustment. Recorded. |
| HfBinary | MATCH | MATCH | — | compared through PathDistance with a horizontal raster. |
| HfForward | MATCH | MATCH | — |  |
| HfInverseLinear | MATCH | MATCH | — |  |
| HfLinear | MATCH | MATCH | — |  |
| HfTable | MATCH | MATCH | — |  |
| HighestPosition | MATCH | MATCH | — |  |
| Hillshade | REFUSED | DIFF | fixed (partly) | Pro truncates the shade (217.65 → 217; grapy rounded, one higher in 1778 cells) and writes NoData on the ring as Aspect. SHADOWS stays refused (the shadow model is not documented). |
| Idw | DIFF | DIFF | recorded | the Variable radius matches. RadiusFixed(150, 3): 50 cells differ where fewer than 3 points lie within 150 m — Pro then takes more than the 3 nearest (4–5, out to about 1.05–1.09 × the third's distance); the widening rule is undocumented. |
| InList | MATCH | MATCH | — |  |
| InspectTrainingSamples | PRO-ERROR | PRO-ERROR | not comparable | Pro refuses with ERROR 001648 'Rasters do not overlap' for an image, its own Maximum Likelihood .ecd trained on it and training polygons inside it (two rasters tried); cause not found. |
| Int | MATCH | MATCH | — |  |
| InterpolateShape | DIFF | MATCH | fixed | Pro keeps lines that run to or along the raster's edge (grapy dropped 3 of 6: a vertex on the right or bottom edge was off the grid), holding the surface flat from the outer centres to the edge; and densifies each segment into floor(length / step) + 1 equal pieces (61 for 600 m at 10 m; grapy 60). Both in the shared sampler and ddd.InterpolateShape, which sa's answers. |
| IronOxide | MATCH | MATCH | — |  |
| IsNull | MATCH | MATCH | — |  |
| KernelDensity | DIFF | MATCH | fixed | Pro lays the grid as Idw does (lower-left cell centred on the points' corner, round(extent/cell) + 1 cells) and writes 0 where no point is within the radius; grapy put the corner on the extent and wrote NoData. |
| Kriging | DIFF | DIFF | recorded | with the semivariogram fitted (lag 20 only), every cell differs (max 49.6): the fitting is grapy's own. Grid matches. |
| KrigingModelOrdinary | DIFF | DIFF | recorded | with every parameter given, 2735 of 2750 cells match; 15 scattered cells differ (max 3.4) — the 12-nearest search at equal distances, not settled. |
| KrigingModelUniversal | DIFF | DIFF | recorded | as KrigingModelOrdinary: the same 15 cells (max 22.8). |
| LessThan | MATCH | MATCH | — |  |
| LessThanEqual | MATCH | MATCH | — |  |
| LessThanFrequency | MATCH | MATCH | — |  |
| LineDensity | DIFF | MATCH | fixed | grid as KernelDensity (lines over 0…600 m give 61 cells from −5 m) and 0, not NoData, where no line is within the radius. |
| LineStatistics | DIFF | MATCH | fixed | grid as KernelDensity; LENGTH is Σ length × field value, a line touching the circle giving 0 (Pro within 3·10⁻⁵ everywhere); grapy summed lengths alone. |
| LinearUnmixing | PRO-ERROR | PRO-ERROR | not comparable | Pro takes the spectral profile as a JSON or .ecd file (a polygon feature class is read as JSON and refused); grapy refuses both (their layouts are not published) and takes polygons. No input both accept. |
| Ln | MATCH | MATCH | — |  |
| Log10 | MATCH | MATCH | — |  |
| Log2 | MATCH | MATCH | — |  |
| Lookup | DIFF | MATCH | fixed | an integer GeoTIFF with no attribute table: Pro answers Lookup of Count from the table it builds, as floating point (Count is a Double); grapy refused (no table). |
| LowestPosition | MATCH | MATCH | — |  |
| MSAVI | DIFF | MATCH | fixed | grapy wrote ½(2(NIR + 1) − √…), half a unit high everywhere; Pro and the page: ½(2 NIR + 1 − √((2 NIR + 1)² − 8(NIR − Red))). BandArithmetic method 4 had the same error. |
| MTVI2 | MATCH | MATCH | — |  |

## Fixes that reach beyond these names

- **Slope** (the next agent's range) shares Aspect's and Hillshade's gradient: its NoData ring and hole rim were
  measured by probe (236 ring cells) and fixed with them.
- **FindArgumentStatistics** on bands shares ArgStatistics' code: 0-based in Pro too (probe), fixed with it.
- **NumPyArrayToRaster** / **RasterToNumPyArray** (top level): 3-D arrays and multiband rasters, as documented.
- **ddd.InterpolateShape** is the tool `sa.InterpolateShape` answers; the edge sampling is the shared `sa._sample`,
  which ExtractValuesToPoints (INTERPOLATE), ExtractMultiValuesToPoints and UpdateFeatureZ also use.
- **PointDensity** and **PointStatistics** share the density grid but were *not* measured and keep grapy's layout and
  NoData; they very likely follow KernelDensity (grid centred on the points' corner, 0 not NoData) — for the next
  agent. **NbrCircle** in MAP units is still refused (FocalStatistics' case uses CELL units).
- The harness now leaves out an output a case named but no tool wrote (an option one side refuses, through the case's
  `_try`); compare.py reports it as written on the other side only.

## Open differences worth a debt row

1. Multidimensional rasters are always F64 in grapy; Pro keeps F32 (Aggregate, Anomaly, Foreach/Remap differ only by
   that). Carrying the source's cell type through `_mdim`: about a day.
2. AggregateMultidimensionalRaster's slice value: Pro writes a time range clipped to the data, grapy the interval's
   start (ADR-236 §5.3 INFERRED, now contradicted).
3. Line properties of AddSurfaceInformation (Z_MIN … AVG_SLOPE, SURFACE_LENGTH) are refused on a raster: ~1 day.
4. Contour's polygon types (CONTOUR_POLYGON, ContourMin/ContourMax): ~1 day; Hillshade SHADOWS, Fill z_limit,
   FlowDirection's drop raster and flats: undocumented.
5. FlowAccumulation and FlowLength refuse a loop (a two-cell pit) that Pro accumulates; FlowDirection differs in the
   one cell beside it.
6. Idw's Fixed radius widening below the minimum number of points, and the kriging 12-nearest search at ties.
7. Pro defects, not copied: EqualToFrequency with a constant in the list; GenerateMultidimensionalAnomaly's signed
   percent difference (and its variable name, a choice not yet aligned).
8. ExportTrainingDataForDeepLearning's layout (12-digit names, world files, partial edge tiles): the names and edge
   tiles are small.
9. EQUIVALENTs whose cells differ: AreaSolarRadiation, ClassProbability, DarcyFlow, DistanceAccumulation/Allocation,
   EditSignatures, ElevationVoidFill, Kriging's fitted semivariogram, ComputeSegmentAttributes (via Segment Mean
   Shift), ContourWithBarriers (Pro extrapolates to the raster's edge).
10. HeatIndex: 88 cells, up to 0.65 °F, in the NWS high-humidity adjustment region, by an adjustment that is not the
    NWS's. ExtractByRectangle's rule for an edge through a row of cell centres.
