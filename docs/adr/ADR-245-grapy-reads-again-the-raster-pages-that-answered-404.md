# ADR-245 — grapy builds the raster functions whose pages answered 404, from the pages at their new addresses, and refuses the display renderings by what each page leaves out

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-31, under the owner's round-two instruction (*"go on until everything is done, then go back to the start and look at the rest once more"*): every refusal that carries a reason is read again, and a reason written quickly may be wrong. The choices the pages leave open are INFERRED and listed for confirmation in §5.8 and [Q-214](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

On 2026-10-29 [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md)'s classification refused about 40
`arcpy.ia`, `arcpy.sa` and `arcpy.ddd` names with *"its reference page could not be read (404 on 2026-10-29)"*, six
`arcpy` names with *"its documentation could not be found"*, and twelve `arcpy.ia`/`arcpy.sa` names with *"it makes a
display rendering, and its page does not define the output values"*. A 404 on one day is a fact about a URL, not about
the name: Esri moved its Pro documentation from pro.arcgis.com to doc.esri.com during 2026, and pro.arcgis.com's
addresses now redirect to doc.esri.com slugs that are not always the old ones. A refusal grounded on a URL that has
moved is a refusal with the wrong reason.

## 2. Alternatives considered

### Alternative A — find each page again, build what it defines, refuse what it leaves out by name (chosen)

**Argument for.** The tables of contents doc.esri.com serves (`arcpy/toc.json`, `tool-reference/toc.json`,
`help/toc.json`) list every page under its current slug; read once, they answer where each name went. Most of the
names turned out to be documented under slugs the old guesses did not try — `heat-index.html`, `gnvdi.html` (sic),
`linearspectralunmixing.html`, `applyenvironment-function.html`, `classes/rasterinfo-class.html`,
`charts/guide.html` — and the raster function pages they point to give the formulas.

**Argument against.** A name built from a page that defers to a raster function inherits that page's gaps, and those
are INFERRED rather than documented.

### Alternative B — keep the refusals and only re-date them

**Argument for.** Cheap; the reason stays true for the URL tried.

**Argument against.** It refuses names that are documented, which is the shape ADR-224 §5.6 and the owner's ruling
forbid: the reason must belong to the name.

## 3. Counterarguments to the preferred option

Two names are built from **ArcMap's** reference (desktop.arcgis.com), because Pro's documentation no longer has a page
and Pro's arcpy still answers the name: `arcpy.GetUTMFromLocation`, and the legacy tools `arcpy.ddd.SurfaceLength` and
`SurfaceSpot`, which ArcMap's page marks as *"a deprecated tool that has been superseded by the Add Surface
Information tool"*. Pro may have changed them since; ArcMap's page is the only public statement of what they do.

## 4. Evidence

Every page below was read on 2026-10-31 at `https://doc.esri.com/en/arcgis-pro/latest/` unless another host is named.

| Claim | Evidence | Source |
|---|---|---|
| The 404 pages are back under new slugs | `arcpy/toc.json` lists HeatIndex, WindChill, GNDVI, UnitConversion, SpectralConversion, VectorField, VectorFieldRenderer, ColormapToRGB, ApplyEnvironment, ExportRasterFunctionTemplate, LabelBadBands, Resample, ZonalRemap, ElevationVoidFill, LinearUnmixing, Mensuration, PixelBlock, PixelBlockCollection, RasterInfo, RasterCellIterator, Speckle, Guide, TimeBinningProperties; `tool-reference/toc.json` lists InList | the tables of contents |
| HeatIndex's and WindChill's formulas | given in full on the arcpy pages and the raster function pages, with Steadman (1971, 1979) and the NWS cited | `arcpy/image-analyst/heat-index.html`, `wind-chill.html` |
| Speckle names six published filters | Lee, Enhanced Lee, Frost, Kuan, Gamma MAP, Refined Lee, with their parameters and the raster function's defaults | `arcpy/spatial-analyst/speckle.html`; `help/analysis/raster-functions/speckle-function.html` |
| Color Model Conversion gives the HSV scales | hue 0–240 with six listed values (magenta 201), saturation and value 0–255 | `help/analysis/raster-functions/color-model-conversion-function.html` |
| Contrast And Brightness gives no formula | Pro's page and ArcMap's give only the offsets' range, −100 to 100 | the raster function pages, Pro and ArcMap |
| Aspect-Slope gives only output classes | below 20 flat; 21–30, 31–40, 41 and above for slope saturation | `aspect-slope-function.html` |
| MNDWI, FloatDivide, FloorDivide, GetPCSFromDataSource, ViewContext have no page | 404 at doc.esri.com (latest), pro.arcgis.com (latest, 3.3; for the divides 2.9–3.5) and desktop.arcgis.com; no table of contents names them; a web search found none; the Wayback Machine was offline | §5.7 |
| GetUTMFromLocation's answer | *"a WGS84 UTM zone SpatialReference … in the form WGS_1984_UTM_Zone_XXH"*, example `GetUTMFromLocation(-100.9, 40)` | desktop.arcgis.com `analyze/arcpy-functions/getutmfromlocation.htm` |
| SurfaceLength's and SurfaceSpot's parameters | `arcpy.ddd.SurfaceLength(in_surface, in_feature_class, {out_length_field}, {sample_distance}, {z_factor}, {method}, {pyramid_level_resolution})`; raster method BILINEAR only; default sampling the cell size; SurfaceSpot's field defaults to `Spot` | desktop.arcgis.com `tools/3d-analyst-toolbox/surface-length.htm`, `surface-spot.htm` |
| Mensuration measures through a sensor model | point, distance (with azimuth), area and perimeter on the ellipsoid, and height base-to-top or by shadows | `arcpy/image-analyst/mensuration-class.html` |
| Tests | `src/grapy/tests/test_rasterfns_adr245.py`: the NWS heat index table (90 °F, 50 % → 95) and wind chill chart (0 °F, 15 mph → −19), the Spectral Conversion page's matrix, the Color Model Conversion page's six hues, the RasterCellIterator page's two examples, GetUTMFromLocation's example, and hand-computed answers for the rest | 21 tests |

## 5. Decision

grapy builds every name whose page defines its answer, as an equivalent where the page names a method without
specifying it (ADR-224 §5.6), and refuses the rest with a reason that names what that page leaves out.

### 5.1 Built to their pages

`arcpy.ia` and `arcpy.sa`: **HeatIndex**, **WindChill** (the pages' formulas, units converted through kelvin),
**UnitConversion** (the units' exact definitions, NIST SP 811), **GNDVI** (`(NIR − Green)/(NIR + Green)`, defaults 5 and
2), **SpectralConversion** (the page's row-by-row matrix), **VectorField** (the raster function's conversions),
**InList** (the tool page), **ZonalRemap**, **ColormapToRGB** (the colour table the raster's file carries),
**ApplyEnvironment** (extent, cell size, mask, snap raster checked, constants), **RasterInfo** (arcpy.RasterInfo, which
grapy already held), and in `arcpy.sa` **RasterCellIterator**; in `arcpy.ia` **PixelBlock**, **PixelBlockCollection**
and **Mensuration** (on images with RPCs, ADR-234's sensor model); `arcpy.Mensuration` is the same class;
`arcpy.GetUTMFromLocation`; `arcpy.ddd.SurfaceLength` and `SurfaceSpot` from ArcMap's pages.

### 5.2 Built as equivalents

| Name | Method | Source |
|---|---|---|
| Speckle | the six filters as `arcpy.ia.Despeckle` builds them (ADR-237) | Lee 1980, 1981; Lopes et al. 1990; Frost et al. 1982; Kuan et al. 1985 |
| ElevationVoidFill | inverse distance weighting from each void's border (the page's mean → plane fit → IDW cascade has no threshold) | Shepard 1968 |
| LinearUnmixing | least squares; sum-to-one by Lagrange; non-negative by NNLS; both by FCLS | Keshava and Mustard 2002; Lawson and Hanson 1974; Heinz and Chang 2001 |
| Resample | nearest, bilinear, Keys cubic (a = −0.5), majority 4×4, average/min/max over covered centres, vector average | Keys 1981; Parker et al. 1983 |
| ColorspaceConversion | Smith's hexcone on the page's scales | Smith 1978 |

### 5.3 Refused, each by what its page leaves out

| Name | Reason |
|---|---|
| ia/sa Colormap | the answer is the input with a colour map attached, which grapy's rasters do not carry; named maps described only in words |
| ia/sa Render | a rendering rule for display; the page does not say what pixel values a saved rendered raster holds |
| ia/sa ShadedRelief | how the elevation colours and the hillshade are merged, the default ramp and the SCALED equation are not given |
| ia/sa AspectSlope | only the output classes are given, not how aspect and slope are coded into one value |
| ia/sa ContrastBrightness | no formula on Pro's or ArcMap's page |
| ia/sa VectorFieldRenderer | its output is symbols drawn over thinned tiles; no pixel values defined |
| ia ExportRasterFunctionTemplate | grapy's rasters carry no function chain to export; `.rft.xml` unpublished |
| ia LabelBadBands | its only effect is the IsBadBand band property, which grapy's rasters do not carry |
| ia MNDWI; ia/sa FloatDivide, FloorDivide; arcpy GetPCSFromDataSource, ViewContext | no page anywhere (URLs tried and date in each reason) |
| arcpy Guide, TimeBinningProperties | `arcpy.charts` classes (documented); charts are drawn by Pro's application, and `arcpy.Chart` is refused for that reason |

### 5.4 Mensuration

The ground height of a measured point is the RPC's height offset; `height` solves for the ellipsoidal height at which
the top point's ray passes over the base's ground position (Brent's method), and answers no value when it misses by
more than a pixel, as the page allows. Shadow heights are refused: they need sun angles from metadata grapy does not
read.

### 5.5 GetUTMFromLocation

Zone ⌊(λ + 180)/6⌋ + 1, N at and north of the equator, EPSG 326xx/327xx, named in the documented form.

### 5.6 SurfaceLength

The default field name, which the page does not give, is `SURFACE_LENGTH` (AddSurfaceInformation's name for the same
property); a polygon's length is its boundary's.

### 5.7 Pages tried and not found (2026-10-31)

- MNDWI: `arcpy/image-analyst/mndwi.html`, `arcpy/spatial-analyst/mndwi.html` (doc.esri.com latest), the same `.htm`
  at pro.arcgis.com latest and 3.3.
- FloatDivide, FloorDivide: `tool-reference/spatial-analyst/float-divide`, `floor-divide`, `image-analyst/float-divide`,
  `arcpy/spatial-analyst/floatdivide` at doc.esri.com latest; pro.arcgis.com 2.9, 3.0 … 3.5 and latest; ArcMap's
  spatial analyst toolbox. The tables of contents list only the `/` and `//` operators.
- GetPCSFromDataSource, ViewContext: `arcpy/functions/getpcsfromdatasource.html`, `arcpy/mapping/viewcontext-class.html`
  at doc.esri.com, pro.arcgis.com (latest, 3.3) and desktop.arcgis.com; a web search.

### 5.8 INFERRED choices ([Q-214](../open-questions.md))

- (a) ColorspaceConversion: hue degrees × 240/359, the one factor that reproduces all six hues the page lists (240/360
  gives magenta 200, not 201); rounding half up; the inverse uses the same factor, so a colour comes back within a few
  levels.
- (b) ZonalRemap: *above the minimum* includes it and *below the maximum* excludes it (min ≤ v < max); the first matching
  record wins; `background_value` is the answer where a pixel is not remapped at all — NoData in the value raster or a
  zone with no record — and `default_output_value` where a zone has records and none matches.
- (c) HeatIndex and WindChill apply the formula everywhere, without the NWS adjustments and validity limits the pages
  do not give.
- (d) UnitConversion needs both units (their default is None); VectorField's direction is the way the vector points;
  ColormapToRGB makes unmapped values NoData.
- (e) Resample: an empty type is NearestNeighbor; the output grid is anchored at the top-left corner and covers the
  extent in whole cells; a centre on a cell edge takes the cell right of and below it (also ApplyEnvironment's cell
  size); NoData in an interpolation's reach is NoData.
- (f) ElevationVoidFill's voids are 8-connected; LinearUnmixing's class spectrum is the mean of the pixels whose centres
  fall in its polygons, classes in order of first appearance.
- (g) PixelBlockCollection: blocks at every stride from the top-left pixel, cut short at the edge; NoData is NaN or
  the raster's NoData value without `nodata_to_values`.
- (h) Mensuration: §5.4; `sensor_name` "RPC"; the height-type constants 1, 2, 3.
- (i) GetUTMFromLocation: the equator is north; the Norway and Svalbard exceptions are not applied.
- (j) RasterCellIterator: rows then columns; a read past the raster's edge (`raster[r, c]` outside it) is NaN — a change
  to `Raster.__getitem__`, which answered a negative index from the other side of the raster before.
- (k) SurfaceLength: §5.6; SurfaceSpot and SurfaceLength take the legacy pages' parameters as they stand in ArcMap.

## 6. Consequences

- **State.** None: the functions compute on rasters and features the tool is given, and SurfaceLength and
  SurfaceSpot write one field into the input, as documented. Nothing in the catalogue or on the server.
- Against `grapy-surface` at e56366f7, all modules move from 1,304 built (156 equivalents) and 870 refused to 1,344
  built (166 equivalents) and 830 refused, of 2,174: `arcpy` 102 → 104, `arcpy.ddd` 104 → 106, `arcpy.ia` 206 → 225
  (equivalents 29 → 34), `arcpy.sa` 208 → 225 (equivalents 21 → 26).
- No refusal says *404 on 2026-10-29*, *could not be found* or *display rendering* any more.
- **Negative.** Two names rest on ArcMap's page. A Raster read past its edge now answers NaN instead of wrapping.
- **Found, then changed.** The `/ (Division) operator` page now says *"the output will always be a floating-point value
  … if 3 is divided by 2, the output is 1.5"*; grapy's `Raster.__truediv__` followed the earlier wording (two integers divide
  as integers, `_raster.py`'s module docstring). Changed on branch grapy-a, 2026-10-31: `/` is always floating point. The
  Divide *tool* pages (Spatial Analyst and 3D Analyst, read the same day) still document integer division of two
  integers, so `arcpy.sa.Divide`, `arcpy.ia.Divide` and `arcpy.ddd.Divide` keep it — two names, two documented rules.
- **Ports created.** None; numpy, scipy, rasterio and pyproj, already held.

## 7. Assumptions this decision rests on

- doc.esri.com's tables of contents list every page Esri publishes for Pro's arcpy and tool reference.
- ArcMap's reference describes the legacy names Pro's arcpy still answers.

## 8. Dependencies

[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) (§5.6 equivalents),
[ADR-234](ADR-234-grapy-holds-terrain-datasets-and-ortho-mapping.md) (the RPC model Mensuration uses; the TIN surfaces),
[ADR-237](ADR-237-grapy-holds-sar-data-and-motion-imagery-metadata.md) (the speckle filters),
[ADR-229](ADR-229-grapy-holds-las-datasets-and-tins.md) (triangulated surfaces).
Clean room (CLAUDE.md §5): built from the public pages in §4 and the cited papers; no Esri implementation read.

## 9. Revisit triggers

- Esri publishes a page for MNDWI, FloatDivide, FloorDivide, GetPCSFromDataSource or ViewContext.
- A Pro comparison answers differently on an INFERRED choice of §5.8.
- grapy's rasters gain colour maps or band properties (Colormap and LabelBadBands become buildable).

## 10. Dissent

None recorded.

## 11. Conditions

1. **ColorspaceConversion's hue scale and ZonalRemap's threshold ends compared with ArcGIS Pro** (§5.8 (a), (b)): one
   raster through each in Pro 3.7. Needs a Pro session.
2. **The owner answers [Q-214](../open-questions.md).**
