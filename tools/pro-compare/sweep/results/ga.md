# arcpy.ga against ArcGIS Pro 3.7 — 2026-11-08

The 26 names grapy builds in `arcpy.ga`, one case each (`cases/ga.py`), run under Pro 3.7's arcpy and under grapy.
**First run: MATCH 10, DIFF 9, SCHEMA 2, REFUSED 2, PRO-ERROR 2, BOTH-ERROR 1. Now: MATCH 23, DIFF 2, BOTH-ERROR 1.**

The layers compared are IDW, global and local polynomial and radial basis layers written to a `.lyrx` path (Pro's layer
file, grapy's JSON under that name, ADR-233 §5.2). Kriging is not compared on a shared input: Pro makes a kriging layer
only from a model XML, whose form is not published, and grapy reads only its own JSON. The two first-run PRO-ERRORs
were the case's (IDW3D and NearestNeighbor3D take their spacings as linear units, "100 Meters").

Most of the first run's DIFFs had one cause: **the output grid**. Pro puts the first cell's centre on the points'
lower-left corner and takes round(width / cell) + 1 columns and round(height / cell) + 1 rows (measured at nine cell
sizes, 4.9 m to 47 m and the default shorter side / 250); grapy put the grid's top-left corner on the points' and
took ceil(width / cell). Once the grid was Pro's, every interpolator matched cell for cell — including
LocalPolynomialInterpolation and RadialBasisFunctions, which grapy builds as EQUIVALENTs.

Every fix is pinned in `src/grapy/tests/test_pro_sweep_ga.py`. Tests in `test_ga.py` and `test_geostat.py` that pinned
the old grid, the old field names and "1.0" were changed, each with a comment saying Pro was measured.

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| CompareGeostatisticalLayers | SCHEMA | MATCH | fixed | Pro's table: GA_LYR, RANK, DESCR (the method's name, "Inverse Distance Weighted" …), INCLUDED, RMSE, ME, MAX_ERROR, PERC_ERROR = (1 − RMSE / SD) × 100 with the sample SD; AVERAGE_RANK adds RMS_RANK, ME_RANK, MAXE_RANK (Integer) and AVE_RANK; rows in rank order. grapy wrote Layer, Description, Count, MeanError, MaxAbsError … |
| CrossValidation | MATCH | MATCH | — |  |
| CrossValidationResult | DIFF | DIFF | recorded | count, meanError, rootMeanSquare match. For an IDW layer Pro answers the *string* 'NaN' for meanStandardized, averageStandard, rootMeanSquareStandardized, percentIn90Interval and averageCRPS; grapy answers None, as its docstring says the class page does (the page could not be re-read: 404 at doc.esri.com). Not copied. |
| GACalculateZValue | MATCH | MATCH | — |  |
| GACreateGeostatisticalLayer | DIFF | MATCH | fixed | the grid (above); the layer itself matched |
| GAGetModelParameter | REFUSED | MATCH | fixed | IDW's `/model[@name = 'IDW']/value[@name = 'Power']` answered (Pro: "3"; 045015 on a layer of another method); a whole number is written without ".0", as Pro writes it |
| GALayerToContour | DIFF | DIFF | fixed (partly) | Pro writes one feature per value with Classes (1 … from the lowest) and Value; grapy wrote sa.Contour's Id and Contour, one feature per line, highest first — now Pro's. The lines still differ by up to 10 m: Pro's DRAFT contours come from a coarser grid of its own (rows 7.2544 m apart from the points' top on this input), whose rule is not documented. Recorded. |
| GALayerToGrid | DIFF | MATCH | fixed | the grid |
| GALayerToPoints | SCHEMA | MATCH | fixed | ALL: the locations' fields, Included, Predicted, Error, and no Source_ID; FID_ONLY: the z field's copy, Predicted, Error, Source_ID, Included (probe). grapy wrote Source_ID first in both modes. |
| GALayerToRasters | DIFF | MATCH | fixed | the grid |
| GAMovingWindowKriging | BOTH-ERROR | BOTH-ERROR | not comparable | needs a kriging model source (Pro 045028 for an IDW layer; grapy refuses it too), and Pro makes kriging only from model XML (above) |
| GANeighborhoodSelection | MATCH | MATCH | — |  |
| GASetModelParameter | REFUSED | MATCH | fixed | as GAGetModelParameter; the model written and made into a layer again matches cell for cell |
| GeostatisticalDatasets | MATCH | MATCH | — |  |
| GlobalPolynomialInterpolation | DIFF | MATCH | fixed | the grid |
| IDW | DIFF | MATCH | fixed | the grid |
| IDW3D | PRO-ERROR | MATCH | fixed | case: spacings as linear units. Then the netCDF's vertical dimension and coordinate are `z` in Pro (grapy `elevation`, ADR-238 §5.2 INFERRED), and the cross-validation features carry Source_ID and Included after Measured, Predicted, Error. Voxel values, axes and the derived count, mean error and RMS matched from the start. |
| LocalPolynomialInterpolation | DIFF | MATCH | fixed | the grid; the EQUIVALENT's cells then equal Pro's |
| NearestNeighbor3D | PRO-ERROR | MATCH | fixed | as IDW3D (the `z` dimension) |
| RadialBasisFunctions | DIFF | MATCH | fixed | the grid; the EQUIVALENT's cells then equal Pro's (multiquadric, σ 20) |
| SearchNeighborhoodSmooth | MATCH | MATCH | — |  |
| SearchNeighborhoodSmoothCircular | MATCH | MATCH | — |  |
| SearchNeighborhoodStandard | MATCH | MATCH | — |  |
| SearchNeighborhoodStandard3D | MATCH | MATCH | — |  |
| SearchNeighborhoodStandardCircular | MATCH | MATCH | — |  |
| SubsetFeatures | MATCH | MATCH | — | 100 % (management.SubsetFeatures, the same tool) |

## Fixes

- `ga._grid` — the output grid of every interpolator and layer reader (IDW, GlobalPolynomialInterpolation,
  LocalPolynomialInterpolation, RadialBasisFunctions, GALayerToGrid, GALayerToRasters, GALayerToContour).
- `ga.GALayerToContour` / `ga._write_classes` — Classes and Value, one feature per value, lowest first.
- `ga.GALayerToPoints` — Pro's fields and order in both modes.
- `ga.CompareGeostatisticalLayers` — Pro's table (`_DESCRIPTIONS`, `_RANK_FIELDS`).
- `ga._PATHS` / `ga.GAGetModelParameter` — IDW's Power; whole numbers without ".0".
- `_voxel._write` and `_voxel.IDW3D` — the `z` dimension (both voxel tools), Source_ID and Included in the
  cross-validation.

## Open differences worth a debt row

1. **GA Layer To Contour's DRAFT grid.** Pro contours a coarser grid than the layer's default (on the sweep's points
   its rows are 7.2544 m apart, against the 1.98 m default), so its lines differ from grapy's by up to 10 m. The rule
   is not documented; finding it means probing several extents and cell sizes: about half a day.
2. **CrossValidationResult's missing statistics.** Pro answers the string 'NaN' where a layer has no standard errors
   (IDW) and for EBK's interval and CRPS statistics; grapy answers None. Decide which a script should see once the class
   page can be read again.
3. **Kriging is not compared** on a shared input (above), nor GAMovingWindowKriging. A kriging comparison needs a model
   both sides read — the Esri model XML is not a published format — and ADR-233's earlier Kriging comparison through
   arcpy.sa remains the evidence.
4. Rank and standard-error column names in CompareGeostatisticalLayers for STANDARD_ERROR and PRECISION, and DESCR for
   kriging and cokriging layers, are INFERRED (kriging layers cannot be made here).
