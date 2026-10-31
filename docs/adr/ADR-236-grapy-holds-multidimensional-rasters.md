# ADR-236 — grapy holds multidimensional rasters

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` for the model and its netCDF form · `HIGH` for the tools whose pages give the answer (aggregation, anomalies, argument statistics, moving statistics, compute change's difference and relative difference), which the tests check by hand · `LOW` for agreement with Pro where the pages draw the formula as an image or leave the layout out (the trend fits, CCDC, LandTrendr, the change analysis raster, the time held in a cell) |
| **Decided** | 2026-10-31, by owner decision: the multidimensional and change names of `arcpy.ia` and `arcpy.sa` may no longer be refused as *"needs a data model this server does not hold yet"*; grapy gets a multidimensional raster model and the tools are built on it ([ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.7). The storage form and the choices below are INFERRED and listed for confirmation in §5.10 and [Q-204](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Twenty-four `arcpy.ia` names, five of them also `arcpy.sa` names, were refused with one shared sentence: *"it needs a data
model this server does not hold yet (ADR-224 group 2)"*. That is a schedule, not a reason, and ADR-224's guard forbids
it. The names are the multidimensional analysis toolset (Aggregate Multidimensional Raster, Find Argument Statistics,
Generate Multidimensional Anomaly, Generate Trend Raster, Predict Using Trend Raster, Dimensional Moving Statistics,
Multidimensional Principal Components, Multidimensional Raster Correlation, Interpolate From Spatiotemporal Points),
their raster-function twins (Anomaly, ArgStatistics, GenerateTrend, PredictUsingTrend, TrendToRGB, ComputeChange,
DetectChangeUsingChangeAnalysis), the change detectors (Analyze Changes Using CCDC and LandTrendr, Compute Change
Raster, Detect Change Using Change Analysis Raster), the `RasterCollection` class, `RasterToXarray` and
`XarrayToRaster`, and InterpolateRasterByDimension.

Each needs one thing grapy's Raster did not have: **variables over dimensions**. Esri's *An overview of multidimensional
raster data* and the `Raster` class page describe it: a variable (temperature, NDVI) has one or more dimensions
(`StdTime`, `StdZ`) and a slice — a two-dimensional raster of one band or several — for each combination of dimension
values. The class reports it through `isMultidimensional`, `variables` (`['temp(StdTime=24)']`), `variableNames`,
`slices` (`[{'variable': 'temp', 'StdTime': '2017-1-15'}, …]`), `mdinfo`, `getDimensionNames`, `getDimensionValues`,
`getDimensionAttributes`, `getVariableAttributes` and the editing methods.

Esri stores the model as CRF (Cloud Raster Format) and every one of these tools' pages says *"output is CRF only"*. CRF's
layout is not published.

## 2. Alternatives considered

### Alternative A — netCDF following the CF conventions (chosen)

grapy writes a multidimensional raster as netCDF with CF 1.8 metadata: `x` and `y` cell-centre coordinates, a `crs`
grid-mapping variable with WKT and GDAL's `GeoTransform`, a coordinate variable per dimension (time as `days since
1970-01-01 00:00:00`, standard calendar), and a data variable per variable with `_FillValue`. netCDF and CF are open
standards (Unidata; cfconventions.org), GDAL reads them, xarray reads them, and Esri's own multidimensional tools take
netCDF as input. **For:** one open format that every other tool in the field reads; no new dependency (netCDF-3 is
written by scipy, which grapy already has; GDAL in the rasterio wheel reads netCDF-3, netCDF-4, HDF5 and GRIB). **Against:**
netCDF-3 has no compression and a 4 GB variable limit with 64-bit offsets; a script that names `out.crf` must be
changed to `out.nc`.

### Alternative B — a folder of GeoTIFFs and a JSON manifest

One GeoTIFF per slice and a manifest of variables and dimension values. **For:** each slice opens in any GIS.
**Against:** a format nobody else reads as one dataset; the manifest is grapy's invention where a standard exists.

### Alternative C — write CRF

**Against:** its layout is not published; writing it would mean inventing one and calling it Esri's.

### Alternative D — Zarr

GDAL in the wheel reads and writes Zarr. **For:** chunked and compressed. **Against:** rasterio does not expose GDAL's
multidimensional API to write it; xarray would need the `zarr` package; nothing in Esri's tools' input lists names it.

## 3. Counterarguments to the preferred option

A user tool written for Pro saves `out.crf`, and grapy refuses that path by name rather than writing netCDF under a CRF
name — so the tool needs editing. Writing netCDF silently under `.crf` would let the script run and then hand the next
reader a file whose extension lies; the refusal names `.nc` instead. netCDF-3's size ceiling is real but far above what a
fenced user tool's run directory holds (ADR-223 §5.5).

The equivalents (trend fits, CCDC, LandTrendr) will not give Pro's numbers where the pages leave the algorithm out:
CCDC's stability test and model complexity, LandTrendr's despiking and angle culling are the papers', and Pro may differ
in details it does not publish. They are marked `_runtime.equivalent` and say so in every run (ADR-224 §5.6).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The model is variables × dimensions × slices | `Raster` class page: `variables`, `slices`, `getDimensionNames` … with examples | doc.esri.com, arcpy/classes/raster-object |
| A file grapy writes is read by GDAL and xarray as the same grid and cells | `test_netcdf_written_is_read_by_other_readers_and_back`: rasterio's netCDF driver reads EPSG:3857, the transform and slice 2; xarray (scipy engine) reads the time and the NaN | `src/grapy/tests/test_multidimensional.py` |
| A netCDF-4 file written by GDAL is read | `test_a_netcdf4_file_written_by_gdal_is_read`: three time slices from `NETCDF_DIM_time_VALUES` | same |
| The trend fits are the documented band layouts | Generate Trend Raster page lists Slope, Intercept, Harmonic_sin1 … RMSE and the five Kendall bands | doc.esri.com, tool-reference/image-analyst/generate-trend-raster |
| CCDC and LandTrendr are published algorithms | Zhu, Z. and Woodcock, C. E. (2014), *Continuous change detection and classification of land cover using all available Landsat data*, Remote Sensing of Environment 144, 152–171; Kennedy, R. E., Yang, Z. and Cohen, W. B. (2010), *Detecting trends in forest disturbance and recovery using yearly Landsat time series: 1. LandTrendr*, Remote Sensing of Environment 114, 2897–2910 | papers |
| The Kendall tests | Mann (1945) Econometrica 13(3); Kendall (1975) *Rank Correlation Methods*; Sen (1968) JASA 63(324); Hirsch, Slack and Smith (1982) Water Resources Research 18(1) | papers |
| xarray is Apache-2.0 and pandas BSD-3-Clause | `pip show xarray pandas` in grapy-env, 2026-10-31 | DEPENDENCY-LICENSES.md |

## 5. Decision

1. **grapy holds multidimensional rasters** (`grapy/_mdim.py`): a Cube of Variables on one grid, each Variable its
   dimension names, a coordinate per slice and the slices' cells as (slices, bands, rows, columns) with a NoData mask.
   `Raster(path, True)` on a netCDF (or other GDAL multidimensional) file is the whole dataset and reports every
   property and method of the class page that does not edit a CRF in place; `Raster(path)` is the first variable's first
   slice as a plain raster that still says `isMultidimensional`. A multidimensional Raster is not a two-dimensional
   array: map algebra and the single-raster tools refuse it by name rather than read slice one.
2. **Its form on disk is netCDF-CF** (INFERRED; Alternative A). grapy writes netCDF-3 (64-bit offset) through scipy and
   reads netCDF-3 through scipy and everything else through GDAL. A `.crf` path is refused with its reason. Time is
   `days since 1970-01-01 00:00:00` on the standard calendar; a dimension is time when its coordinate's units say
   `<unit> since <date>` (CF §4.4) and is called `StdTime`; a vertical one (`axis = "Z"` or `positive`) is `StdZ`; a
   slice of several bands carries a `band` dimension and the band names in the variable's `band_names` attribute.
3. **A time held in a cell is a decimal year** (INFERRED): ArgStatistics' and FindArgumentStatistics' dimension value,
   and a change date. A band index held in a cell is one-based, as HighestPosition's is.
4. **An aggregated slice's dimension value is the start of its interval** (INFERRED): the first instant of the hour,
   day, ISO week, dekad, pentad, month, quarter or year; an INTERVAL_VALUE bin's start; an interval range's minimum; a
   recurring interval's first occurrence. A moving window takes the slices there are at either end. A standard deviation
   divides by n.
5. **The trend models are equivalents** (the page draws them as images): ordinary least squares for linear, polynomial
   and harmonic (Zhu and Woodcock's form), with x in days since the first slice for time — so the intercept is the
   fitted value at the first slice, as the Trend to RGB page describes it — and the value less the first otherwise;
   RMSE divides by n; R2 and Slope_P_Value follow RMSE; the slope p-value is the two-sided t-test of the linear term.
   The Kendall tests follow Mann, Kendall, Sen and Hirsch–Slack–Smith. A trend raster records its model in its
   attributes (`grapy_trend` …) so PredictUsingTrend can evaluate it; one made elsewhere is refused.
6. **grapy's change analysis raster is its own** (INFERRED; the pages give its contents, not its bands): one variable,
   `CCDC` (a slice per input slice: per detection band the eight harmonic coefficients and RMSE, then Change and
   Magnitude) or `LandTrendr` (a slice per year: Fitted_Value, Slope, Vertex, RMSE, then Fitted_<band>). Detect Change
   reads that raster and refuses one made elsewhere. CCDC is fitted by ordinary least squares as in the 2014 paper; its
   Tmask is refused. LandTrendr's despiking proportion and angle measure are grapy's reading of the paper.
7. **Compute Change** numbers transitions from 1 in order of (from, to) with `Other` (function) or `No Change` (tool) as
   0, and writes Value, ClassName, Class_From, Class_To, Count, Area (and Red, Green, Blue when both inputs carry
   colours) — INFERRED. BAND_WITH_MOST_CHANGE is the one-based band of the largest absolute difference.
8. **RasterCollection** holds items as dicts with the raster under `Raster`, as the page's examples read them; its STAC
   constructors are refused (no network in a fenced tool, ADR-223 §5.4).
9. **xarray is added** (Apache-2.0, with pandas BSD-3-Clause, already in the server image): RasterToXarray gives a
   Dataset over arcpy's dimension names, then `y` and `x`, with `crs` and `transform` attributes; XarrayToRaster reads one
   back.
10. ~~**The INFERRED choices**~~ **Confirmed 2026-10-31 by owner decision in Q-204** (*"Hepsini onayla"*), for the owner ([Q-204](../open-questions.md)): (a) netCDF-CF as grapy's form, `.crf`
    refused; (b) the CF encoding of time, `StdTime`/`StdZ` naming and `band_names`; (c) a time in a cell as a decimal
    year, a band index from one; (d) an aggregated slice dated at its interval's start, WEEKLY as the ISO week, a
    recurring interval at its first occurrence, interval ranges inclusive at both ends, PERCENTILE's default NEAREST;
    (e) Anomaly's periods calendar periods, GenerateMultidimensionalAnomaly's HOURLY each hour of each day, its default
    interval ALL, Z_SCORE's S over n; (f) ArgStatistics' DURATION in slices, MEDIAN of an even count the lower value, one
    variable of one dimension answering a plain raster; (g) moving windows truncated at the ends; (h) the trend's x, RMSE
    over n, the R2 and p-value band order, no fit statistics on the Kendall bands; (i) TrendToRGB's stretches and constant
    value; (j) the change analysis raster's layout, a change date as a decimal year, a LandTrendr segment's magnitude its
    fitted end less its start and every segment a change, the function's duplicated `min_duration` read as
    `max_duration`; (k) Compute Change's numbering and attribute fields; (l) MultidimensionalPrincipalComponents' table
    fields and covariance over n − 1; (m) the correlation's band order, NoData pairs left out; (n) Interpolate From
    Spatiotemporal Points' MEAN and MEDIAN per cell, a slice dated at its period's start; (o) RasterCollection's
    `fields` order, `reduce`'s call, DAY_OF_WEEK from Monday; (p) RasterToXarray's attributes and NaN for NoData.

**Names, before → after.** `arcpy.ia`: 23 of the 24 built (13 to their pages, 10 as equivalents); `arcpy.sa`: the 5
twins built. Refused with its own reason: **InterpolateRasterByDimension** — arcpy documents no Python signature for it
(there is no arcpy reference page; the raster function page gives display labels only), so its parameter names and
order are unknown. Parameters refused by name inside built tools: AggregateMultidimensionalRaster's CUSTOM,
FindArgumentStatistics' INTERVAL_KEYWORD (its page describes it two incompatible ways), MultidimensionalPrincipal
Components' SPATIAL_REDUCTION (an unspecified random reprojection), InterpolateFromSpatiotemporalPoints' QUADRATIC (an
image-only formula) and trajectory inputs, CCDC's Tmask, TrendToRGB on a CCDC raster, RasterCollection's STAC
constructors and summarizeField's HISTOGRAM.

## 6. Consequences

**State.** None in the catalogue or at runtime: grapy is a library in the image, and a multidimensional raster is a
netCDF file in the run's own directory, held in memory while a tool works on it.

**Positive.** A user tool can read netCDF climate and remote-sensing time series, run Pro's multidimensional and
change workflows under their arcpy names, and hand the result to any netCDF reader.

**Negative.** Scripts that save `.crf` must be edited. netCDF-3 is uncompressed. The trend, CCDC and LandTrendr numbers
will differ from Pro's wherever Pro departs from the papers, and the change analysis raster cannot be exchanged with
Pro. CCDC and LandTrendr run per pixel in Python and are slow on large rasters. The image grows by xarray (pandas is
already there).

**Ports created.** None new: GDAL stays behind rasterio, netCDF-3 behind scipy's reader and writer, both inside
`grapy/_mdim.py`; xarray appears only in RasterToXarray and XarrayToRaster, whose arcpy signatures name it.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A fenced user tool's multidimensional data fits in memory and under netCDF-3's limits | Unvalidated; no measurement |

## 8. Dependencies

**Depends on:** [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) (scope, §5.6 equivalents),
[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) (the fence, no network), [ADR-230](ADR-230-grapy-holds-mosaic-datasets.md)
(mosaic datasets, which RasterCollection refuses as input).

**Depended on by:** none yet.

## 9. Revisit triggers

- Esri publishes CRF's layout: write it beside netCDF.
- A user tool's netCDF exceeds netCDF-3's 4 GB variable limit, or memory: move to netCDF-4 or Zarr through GDAL's
  multidimensional API.
- A Pro run of Generate Trend Raster, CCDC or LandTrendr on a shared series differs from grapy's: compare and record.

## 10. Dissent

None recorded.

## 11. Conditions

1. **Every name of the old group-2 entry for multidimensional and change tools is built or refused with its own
   reason.** **DISCHARGED 2026-10-31**: 23 `arcpy.ia` and 5 `arcpy.sa` names built, InterpolateRasterByDimension refused
   with its own reason; `tools/grapy_refusals.py --check` and `test_interpolate_raster_by_dimension_has_its_own_reason`.
2. **The model reads what others write and others read what it writes.** **DISCHARGED 2026-10-31**: GDAL and xarray read
   grapy's netCDF; grapy reads a netCDF-4 file GDAL wrote (`test_multidimensional.Model`).
3. **The equivalents are compared with Pro** on one shared series — Generate Trend Raster (linear, harmonic,
   Mann-Kendall), Analyze Changes Using CCDC and LandTrendr — and each difference is recorded or removed.
4. **The owner confirms or changes the INFERRED choices** of §5.10 ([Q-204](../open-questions.md)). **DISCHARGED 2026-10-31** — confirmed as built by owner decision in Q-204 (*"Hepsini onayla"*).
