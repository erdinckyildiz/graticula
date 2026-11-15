# arcpy.sa, second half (MajorityFilter to the last) against ArcGIS Pro 3.7

Measured 2026-11-08 and 2026-11-12, case module `cases/sa_2.py`, 142 names (the built `arcpy.sa` names from
`MajorityFilter` to the end of `sorted()`; the first 151 are `cases/sa_1.py`'s). A tool's Raster is returned and
dumped; a second output is saved through `O`. Classes (neighbourhoods, remap tables, radii, Tf, Vf, Time and Topo
classes, WSTable) are compared through the tool that uses them, and their documented properties and `str()` through
the answer. A raster of several bands is saved and then dumped band by band (grapy's RasterToNumPyArray reads one band;
Pro saves a function raster's own `getRasterBands()` band 2 as band 1, so the bands are read from the saved file).

The hydrology cases (SnapPourPoint, StreamLink, StreamOrder, Watershed) run on a pit-free valley DEM built in the
case module: on the shared DEM, Flow Direction writes a two-cell loop at the pit, which grapy's accumulation refuses
(open 9). The back-direction raster OptimalPath* reads (Distance Accumulation's) is not dumped, being another sweep's
name.

First run (Pro's first run against grapy before any fix, with the case corrections of that day): MATCH 71, DIFF 66,
PRO-ERROR 3, BOTH-ERROR 1, REFUSED 1. The PRO-ERRORs were the case: RadiusFixed/RadiusVariable's properties are
`minNumberOfPoints`/`numberOfPoints` in Pro, and Topo to Raster by File's keywords are `EXTENT` and `CELL_SIZE`.
Now: MATCH 94, DIFF 47, BOTH-ERROR 1 — every one below fixed, or recorded with its reason.

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| MajorityFilter | DIFF | DIFF | recorded | FOUR/MAJORITY match; EIGHT/HALF differs in 71 of 3600 noisy cells (the tie and contiguity rules of an EQUIVALENT; open 1) |
| Minus | MATCH | MATCH | — |  |
| Mod | MATCH | MATCH | — |  |
| NBR | MATCH | MATCH | — |  |
| NDBI | MATCH | MATCH | — |  |
| NDMI | MATCH | MATCH | — |  |
| NDSI | MATCH | MATCH | — |  |
| NDVI | MATCH | MATCH | — |  |
| NDVIre | MATCH | MATCH | — |  |
| NDWI | MATCH | MATCH | — |  |
| NaturalNeighbor | DIFF | DIFF | fixed, then recorded | grid fixed: floor(extent / cell) + 1 cells per side (54 × 50), not Idw's round + 1 (55 × 50); then 13 of 1932 cells near the hull differ by up to 0.32 (open 12) |
| NbrAnnulus | DIFF | MATCH | fixed | a cell on the inner radius is in the ring; map units rounded half up to cells; str() as Pro's |
| NbrCircle | REFUSED | MATCH | fixed | map units (refused before) divided by the cell size, rounded half up; str() 'CIRCLE 25 MAP' |
| NbrIrregular | DIFF | MATCH | case | Pro answers inKernelFile in single quotes; the file name is compared |
| NbrRectangle | MATCH | MATCH | fixed | even sides (refused before) reach one cell further right and down; map units; str() |
| Negate | MATCH | MATCH | — |  |
| Nibble | DIFF | DIFF | recorded | 9 of 3600 cells take another nearest value (the tie among equidistant cells; open 1) |
| NotEqual | MATCH | MATCH | — |  |
| ObserverPoints | DIFF | DIFF | recorded | 125 cells differ, the same cells as Viewshed (line-of-sight algorithm; open 2) |
| OptimalPathAsLine | DIFF | MATCH | case | matches; the back-direction raster (Distance Accumulation's, another sweep's name) is no longer dumped |
| OptimalPathAsRaster | DIFF | MATCH | case | as OptimalPathAsLine |
| Over | MATCH | MATCH | — |  |
| PVI | MATCH | MATCH | — |  |
| PathAllocation | MATCH | MATCH | — |  |
| PathBackLink | MATCH | MATCH | — |  |
| PathDistance | MATCH | MATCH | — |  |
| Pick | MATCH | MATCH | — |  |
| Plus | MATCH | MATCH | — |  |
| Point | MATCH | MATCH | — |  |
| PointDensity | DIFF | DIFF | recorded | Pro's grid starts 1.2914 m below and left of the points' corner (54 × 50), grapy's at Idw's; cells differ with it (open 3) |
| PointStatistics | DIFF | DIFF | recorded | the same grid as PointDensity (open 3) |
| PointsSolarRadiation | DIFF | DIFF | recorded | T0 5117.79 vs 5118.02 Wh/m² (0.004 %) — the Fu and Rich EQUIVALENT (open 4) |
| Popularity | MATCH | MATCH | — |  |
| Power | MATCH | MATCH | — |  |
| PrincipalComponents | DIFF | MATCH | fixed | bands projected as they are, not less their means; F32 |
| RTVICore | MATCH | MATCH | — |  |
| RadiusFixed | PRO-ERROR | DIFF | fixed, then recorded | minNumberOfPoints (Pro's spelling) and str() 'FIXED 120 3'; Idw through it differs in 150 cells (Idw is another sweep's name) |
| RadiusVariable | PRO-ERROR | MATCH | fixed | numberOfPoints (Pro's spelling) and str() 'VARIABLE 5 200' |
| Rank | MATCH | MATCH | — |  |
| Raster | MATCH | MATCH | — |  |
| RasterCalculator | MATCH | MATCH | — |  |
| RasterCellIterator | MATCH | MATCH | — |  |
| RasterInfo | MATCH | MATCH | — |  |
| RasterSolarRadiation | DIFF | DIFF | recorded | edge ring NoData in Pro; interior within 0.02 (EQUIVALENT; open 4) |
| ReclassByASCIIFile | DIFF | MATCH | fixed | a file mixing single values and ranges is read, as Pro reads it (refused before) |
| ReclassByTable | MATCH | MATCH | — |  |
| Reclassify | DIFF | MATCH | fixed | a float raster whose every value a row names comes out integer (U8 in Pro) |
| RegionGroup | DIFF | DIFF | recorded | the regions are the same (a one-to-one map of labels); the numbering order differs (open 5) |
| Remap | DIFF | DIFF | recorded | Pro contradicts its page: allow_unmatched behaves as False by default, and replacement_value applies with True, not False (open 6) |
| RemapRange | DIFF | MATCH | fixed | through Reclassify: integer output; str() '90 110 1; 110 130 2' |
| RemapValue | MATCH | MATCH | fixed | str() '1 10; 2 20; 3 NODATA' |
| RemoveRasterSegmentTilingArtifacts | DIFF | DIFF | recorded | follows SegmentMeanShift's difference (open 7) |
| Resample | MATCH | MATCH | — |  |
| RescaleByFunction | MATCH | MATCH | fixed | the rescaling's ends are the function's extremes over the threshold interval, not at the cells |
| RoundDown | MATCH | MATCH | — |  |
| RoundUp | MATCH | MATCH | — |  |
| SAVI | DIFF | DIFF | recorded | Pro's defaults are bands 4 and 3; its page (and grapy) say 7 and 6 (open 8) |
| SRre | MATCH | MATCH | — |  |
| SegMeanShift | DIFF | DIFF | recorded | mean shift segmentation EQUIVALENT; segments and colours differ (open 7) |
| SegmentMeanShift | DIFF | DIFF | recorded | as SegMeanShift (open 7) |
| SetNull | MATCH | MATCH | — |  |
| Shrink | DIFF | DIFF | recorded | 218 of 3600 noisy cells: the replacing value's tie rule (EQUIVALENT; open 1) |
| Sin | MATCH | MATCH | — |  |
| SinH | MATCH | MATCH | — |  |
| Sink | DIFF | DIFF | fixed, then recorded | a pit's two cells pointing at each other are a sink, as in Pro; still DIFF only by the saved flow direction, which differs at one tie cell (Pro 1, grapy 128 — FlowDirection, another sweep's name) |
| Slice | DIFF | DIFF | recorded | EQUAL_INTERVAL matches; EQUAL_AREA puts 2 of 3600 cells in the next zone (Pro's histogram bins; open 10) |
| Slope | DIFF | MATCH | fixed | edge ring NoData; a side's weighted mean over the neighbours present; NoData when a side keeps under half its weight |
| SnapPourPoint | DIFF | MATCH | case | on a pit-free valley DEM: on the shared DEM grapy refuses the loop Flow Direction writes at its pit, where Pro answers (open 9) |
| SolarRadiationGraphics | DIFF | DIFF | recorded | Pro writes sky_size + 1 cells (201 × 201) where grapy writes 200 × 200 (open 4) |
| Speckle | DIFF | DIFF | recorded | Lee filter: 225 of 3600 cells across the raster differ by up to 0.13 (open 11) |
| SpectralConversion | DIFF | MATCH | — |  |
| Spline | DIFF | DIFF | recorded | the Mitas–Mitasova EQUIVALENT gives other values (up to 55 on 10…100 data; open 12) |
| SplineWithBarriers | DIFF | DIFF | fixed, then recorded | grid fixed to cover the barriers' extent (61 × 61); then cells differ, and grapy leaves 334 cells cut off from every point NoData where Pro fills them (minimum-curvature EQUIVALENT; open 12) |
| Square | MATCH | MATCH | — |  |
| SquareRoot | MATCH | MATCH | — |  |
| StreamLink | DIFF | MATCH | case | on a pit-free valley DEM: on the shared DEM grapy refuses the loop Flow Direction writes at its pit, where Pro answers (open 9) |
| StreamOrder | DIFF | MATCH | case | on a pit-free valley DEM: on the shared DEM grapy refuses the loop Flow Direction writes at its pit, where Pro answers (open 9) |
| Stretch | DIFF | MATCH | fixed | min/max are the output's range; F32 not rounded U8; StdDev from the sample σ, held within the min and max |
| Subset | BOTH-ERROR | BOTH-ERROR | not comparable | needs a multidimensional raster; Pro refuses a plain one, grapy refuses a one-slice one |
| SurfaceParameters | DIFF | DIFF | recorded | slope within 0.1°, aspect on flat-ish cells wraps (EQUIVALENT; open 13) |
| TSAVI | MATCH | MATCH | — |  |
| Tan | MATCH | MATCH | — |  |
| TanH | MATCH | MATCH | — |  |
| Test | MATCH | MATCH | — |  |
| TfExponential | MATCH | MATCH | — |  |
| TfGaussian | MATCH | MATCH | — |  |
| TfLarge | MATCH | MATCH | — |  |
| TfLinear | DIFF | MATCH | fixed | through RescaleByFunction's ends |
| TfLogarithm | DIFF | DIFF | recorded | one cell, the minimum: Pro 1.000383, grapy 1 (open 10) |
| TfLogisticDecay | DIFF | MATCH | fixed | decay fell the wrong way (rose); now c / (1 + a·e^(−b(x − lo))) as growth |
| TfLogisticGrowth | MATCH | MATCH | — |  |
| TfMSLarge | MATCH | MATCH | — |  |
| TfMSSmall | MATCH | MATCH | — |  |
| TfNear | MATCH | MATCH | — |  |
| TfPower | MATCH | MATCH | — |  |
| TfSmall | MATCH | MATCH | — |  |
| TfSymmetricLinear | DIFF | MATCH | fixed | through RescaleByFunction's ends (peak 1 between the cells) |
| Threshold | DIFF | MATCH | fixed | cut at the best bin's centre, not its upper edge |
| TimeMultipleDays | DIFF | DIFF | fixed, then recorded | str() 'MULTIDAYS 2026 100 130'; radiation differs 0.45 % (open 4) |
| TimeSpecialDays | DIFF | DIFF | fixed, then recorded | str() 'SPECIALDAYS'; radiation: Pro 5916, grapy 10097 (open 4) |
| TimeWholeYear | DIFF | DIFF | fixed, then recorded | str() 'WHOLEYEAR 2026'; radiation differs 1.2 % (open 4) |
| TimeWithinDay | DIFF | DIFF | fixed, then recorded | str() 'WITHINDAY 172 8 16'; radiation differs 0.004 % (open 4) |
| Times | MATCH | MATCH | — |  |
| TopoBoundary | DIFF | DIFF | recorded | through TopoToRaster (open 14) |
| TopoCliff | DIFF | DIFF | recorded | through TopoToRaster (open 14) |
| TopoCoast | DIFF | DIFF | recorded | through TopoToRaster; Pro also leaves the area outside the coast NoData (open 14) |
| TopoContour | DIFF | DIFF | recorded | through TopoToRaster (open 14) |
| TopoExclusion | DIFF | DIFF | recorded | through TopoToRaster (open 14) |
| TopoLake | DIFF | DIFF | recorded | through TopoToRaster (open 14) |
| TopoPointElevation | DIFF | DIFF | recorded | through TopoToRaster (open 14) |
| TopoSink | DIFF | DIFF | recorded | through TopoToRaster (open 14) |
| TopoStream | DIFF | DIFF | recorded | through TopoToRaster (open 14) |
| TopoToRaster | DIFF | DIFF | recorded | ANUDEM EQUIVALENT: same grid, cells differ by up to 42 m (open 14) |
| TopoToRasterByFile | PRO-ERROR | DIFF | case | the case wrote XYLIMITS/CELLSIZE, which neither side reads (EXTENT, CELL_SIZE); then as TopoToRaster (open 14) |
| TransposeBits | MATCH | MATCH | — |  |
| Trend | MATCH | MATCH | — |  |
| UnitConversion | MATCH | MATCH | — |  |
| VARI | MATCH | MATCH | — |  |
| VectorField | DIFF | MATCH | fixed | direction −180 … 180 as atan2 gives it, not wrapped to 0 … 360 (the case first read Pro's band 2 wrongly: Pro saves a function raster's own getRasterBands() band 2 as band 1) |
| VfBidirHikingTime | MATCH | MATCH | — |  |
| VfBinary | MATCH | MATCH | — |  |
| VfCos | DIFF | DIFF | recorded | PathDistance through it: Pro up to 0.61 lower; the linear factors match, so the cosine curve differs (open 15) |
| VfCosSec | DIFF | DIFF | recorded | defaults match; powers 1, 2: within the VfCos residual (open 15) |
| VfHikingTime | MATCH | MATCH | — |  |
| VfInverseLinear | MATCH | MATCH | — |  |
| VfLinear | MATCH | MATCH | — |  |
| VfSec | DIFF | DIFF | recorded | as VfCos (open 15) |
| VfSecCos | DIFF | DIFF | recorded | Pro applies secPower to the cosine side and cosPower to the secant side, against its page (open 15) |
| VfSymInverseLinear | MATCH | MATCH | — |  |
| VfSymLinear | MATCH | MATCH | — |  |
| VfTable | DIFF | DIFF | recorded | 16 cells differ by up to 0.07 (interpolation at the table's ends; open 15) |
| Viewshed | DIFF | DIFF | recorded | 125 of 3600 cells (R3 line of sight EQUIVALENT; open 2) |
| Viewshed2 | DIFF | DIFF | recorded | Pro leaves 165 more cells NoData (outer radius) and 34 differ (geodesic EQUIVALENT; open 2) |
| Visibility | DIFF | DIFF | recorded | as Viewshed (open 2) |
| WSTable | MATCH | MATCH | fixed | str() as Pro's: the raster quoted, rows by '; ' |
| Watershed | DIFF | MATCH | case | on a pit-free valley DEM: on the shared DEM grapy refuses the loop Flow Direction writes at its pit, where Pro answers (open 9) |
| WeightedSum | MATCH | MATCH | — |  |
| WindChill | MATCH | MATCH | — |  |
| ZonalFill | MATCH | MATCH | — |  |
| ZonalRemap | MATCH | MATCH | — |  |
| ZonalStatistics | MATCH | MATCH | — |  |
| ZonalStatisticsAsTable | DIFF | MATCH | fixed | ALL writes MEDIAN and PCT90; COUNT a double; a second, empty output (the join layer) |

## Open differences worth a debt row

1. **Filters' tie rules** — MajorityFilter (EIGHT/HALF, 71 of 3600 cells), Nibble (9), Shrink (218) on a deliberately
   noisy class raster. Each is an EQUIVALENT whose tie among equal counts or equal distances Pro does not document;
   finding Pro's rule needs a set of hand-built ties measured one at a time (about half a day).
2. **Line of sight** — Viewshed, Visibility and ObserverPoints differ in the same 125 of 3600 cells; Viewshed2 leaves
   165 more cells NoData and differs in 34. grapy's R3 (and geodesic) EQUIVALENTs; Pro's sampling along the sight line
   is undocumented (days).
3. **PointDensity and PointStatistics grid** — Pro's grid starts 1.2914 m below and left of the points' lower-left
   corner (54 × 50 cells on the fixture), where grapy uses Idw's layout; the offset's rule is not found yet (hours, with
   a few point sets).
4. **Solar radiation** — PointsSolarRadiation and the Time classes through it (0.004 % within a day, 0.45 % over days,
   1.2 % a whole year, special days 5916 against 10097 Wh/m²), RasterSolarRadiation (Pro's edge ring is NoData; the
   interior within 0.02), SolarRadiationGraphics (Pro writes sky_size + 1 cells, 201 × 201). The Fu and Rich
   EQUIVALENT; the special-days total and the sky-map size are the two worth chasing first (a day).
5. **RegionGroup numbering** — the regions are identical (a one-to-one map between the two sides' labels) but Pro
   numbers them in another order, apparently as regions close during the scan rather than as they open (hours).
6. **Remap against its page** — Pro treats `allow_unmatched` as False when it is left out (the page's default is
   True), and applies `replacement_value` when it is True, not when it is False as the page says (measured on
   1…6 with ranges [1, 3) and [4, 6)). grapy follows the page; recorded, not copied.
7. **Mean shift segmentation** — SegmentMeanShift, SegMeanShift and RemoveRasterSegmentTilingArtifacts: segments and
   their colours differ (EQUIVALENT; days).
8. **SAVI's default bands** — Pro uses bands 4 and 3 when none are given; its page and grapy say 7 and 6. Recorded,
   not copied.
9. **Loops in a flow direction raster** — Flow Direction (both sides) writes a one-cell pit as two cells pointing at
   each other. Pro's Flow Accumulation, Snap Pour Point, Watershed, Stream Link and Stream Order answer on it; grapy's
   refuse it ("holds a loop"). Sink now flags such a loop as Pro does. Deciding what the accumulation tools do at a
   loop needs Pro's Flow Accumulation measured on a pit (FlowAccumulation is sa_1's name; a few hours across the five
   tools, in the shared `_upstream_order`). Also seen: grapy's FlowDirection differs from Pro's at one tie cell of the
   shared DEM (Pro 1, grapy 128) — for sa_1.
10. **Histogram bins** — Slice EQUAL_AREA puts 2 of 3600 cells in the next zone, and TfLogarithm's lowest cell is
    1.000383 in Pro and 1 in grapy: Pro works from a histogram whose binning is not documented (small; hours).
11. **Speckle (Lee)** — 225 of 3600 cells, spread across the raster, differ by up to 0.13 (the local statistics or the noise variance default; hours).
12. **Interpolators** — Spline (regularized and tension) differs by tens of metres on 10 points; SplineWithBarriers
    now has Pro's grid but differs in value and leaves cells cut off from every point NoData where Pro fills them;
    NaturalNeighbor has Pro's grid and differs in 13 cells near the hull. EQUIVALENTs (days).
13. **SurfaceParameters** — slope within 0.1°; aspect wraps on near-flat cells (EQUIVALENT; hours).
14. **Topo to Raster** — every Topo class is compared through TopoToRaster: same grid, cells differ by up to 42 m;
    TopoCoast also leaves the area outside the coast NoData in Pro. ANUDEM EQUIVALENT (days).
15. **Vertical factors** — PathDistance through VfCos and VfSec is up to 0.61 lower in Pro (the linear factors match
    exactly, so the curve itself differs, by an amount no cosine power explains); VfTable differs in 16 cells by up to
    0.07; VfSecCos in Pro applies `secPower` to the cosine side and `cosPower` to the secant side, against its page
    (Pro's object holds them as given) — recorded, not copied.

Also for sa_1 (not edited here): RadiusFixed's Idw output differs from Pro's in 150 of 2750 cells; BlockStatistics
still refuses map units and even rectangles, which the neighbourhood classes now build; Slope's new edge and NoData
rule was measured on Slope only, but Aspect and Hillshade use the same `_gradients` helper and likely carry it;
ia.MultidimensionalPrincipalComponents says it computes "as sa.PrincipalComponents", which no longer subtracts the
means.

Not comparable: **Subset** — it needs a multidimensional raster; Pro refuses a plain one ("not a multidimensional
raster") and grapy refuses map algebra on a one-slice one; no multidimensional fixture is built on both sides here.
