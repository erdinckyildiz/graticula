# ADR-251 — grapy holds topographic production: its documented tables and workbooks, the World Magnetic Model, and the cartographic tools as equivalents, and refuses what needs a Pro map, a portal or an unpublished format

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-31, by owner decision on scope (*"Tamamını bitirelim artık"*) and [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.7 and condition 5: each group-2 data model gets its ADR before its tools. `arcpy.topographic` was refused whole as *"the topographic production model needs a data model this server does not hold yet (ADR-224 group 2)"* — a schedule, not a reason. Every one of its 55 names is now built, built as an equivalent, or refused with a reason of its own. How a GeoPackage holds what the toolbox keeps, and the rules the pages leave open, are INFERRED and listed for confirmation in §5.8 and [Q-221](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

The Topographic Production toolbox (Topographic Mapping extension) has 55 tools in five groups: cartography (banding,
refinement, features, layout), data management (features, generalization, geodatabase, topology, validation) and
workflow. Its "data model" is less one model than a set of conventions: a FeatureLevelMetadata table, a
cross-reference geodatabase of six tables, a geodatabase-schema workbook of seven worksheets, contour codes in an
`HQC` field, MEF figures, the World Magnetic Model, NGA GeoNames files, and product files Esri ships. Most of these are
documented on public help pages; some are not (the rule workbooks, the visual specification tables, the grids and
graticules XML, the MGCP Technical Reference Documents). About a third of the tools act on an ArcGIS Pro map or layout,
or on a portal.

A toolbox that loads data through a cross-reference, makes contours and spot heights, or computes magnetic variation for
a sheet stopped at the module, though most of what it asked is computable from the pages.

## 2. Alternatives considered

### Alternative A — build what the pages define, as equivalents where they describe a result without its method, and refuse the rest one name at a time (chosen)

The FeatureLevelMetadata table, the cross-reference tables and mapping workbook, the geodatabase workbook's seven
sheets and the MEF rules are documented to the column or the arithmetic; they are built to their pages. The World
Magnetic Model is a published US/UK government model; contouring, medial axes, morphological opening and stroke-based
thinning are published methods. **For:** each name answers what its page says, or says why it cannot. **Against:** the
cartographic tools' results are Pro's own judgement (how a band is smoothed, which tributary is thinned) and an
equivalent's numbers will differ from Pro's (ADR-224 §5.6).

### Alternative B — refuse every tool whose page names the Topographic Mapping product files

Many pages default to paths under the product files (MapIndex, rule workbooks). **For:** literal. **Against:** the files
are defaults, not requirements: every such tool takes its inputs as parameters, and a user can pass their own sheet
index, land layer or mapping workbook. Refusing on a default would refuse what is buildable.

### Alternative C — write Esri-shaped files from inference (topology XML, MGCP metadata XML, WMM.xml)

**For:** files Pro could open. **Against:** a file guessed at would be read by Pro, if at all, in ways no one can check.
Rejected: ExportTopology and ImportTopology write and read **grapy's own documented XML** and say so (§5.8 (h)); the
MGCP and WMM.xml forms are refused by name.

## 3. Counterarguments to the preferred option

Twenty-one equivalents is a large share of one module. The banding, thinning and diagram tools exist to make a product
that passes a specification's review; grapy's bands and thinned rivers are defensible, cited and tested, but no
cartographer has compared them with Pro's. The cross-reference geodatabase page gives the tables and columns but not
their types; a GeoPackage written here is not a file geodatabase Pro's LoadData would open. The GeoPackage keeps its
object ID as `fid`, which SQLite compares without case to the documented output field `FID` of PolygonToCenterline and
IdentifyNarrowPolygons, so those two outputs key on `OBJECTID` instead (§5.8 (g)).

## 4. Evidence

All pages read 2026-10-31 (doc.esri.com, ArcGIS Pro 3.7 "latest"; pro.arcgis.com redirects there). Tool pages are under
`https://doc.esri.com/en/arcgis-pro/latest/tool-reference/topographic-production/` (TP/ below). The research notes are
the session's `research-topographic.md`; the pages re-read for this ADR are marked *re-read*.

| Claim | Evidence | Source |
|---|---|---|
| The toolbox's tools and groups; Workflow Manager (Classic) tools removed at Pro 3.5 | Toolbox overview | TP/an-overview-of-the-topographic-production-toolbox.html |
| FeatureLevelMetadata: `MetadataFavoriteName` Text 50, `MetadataTokens` Text 255 JSON of `USER`, `UTC_DATE`, `LOCAL_DATE` | Help page | TP/featurelevelmetadata-table.html |
| ApplyFeatureLevelMetadata applies a favourite to same-named fields; types must match or the tool stops | Tool page | TP/apply-feature-level-metadata.html |
| CalculateDefaultValues replaces nulls with the data model's defaults | Tool page | TP/calculate-default-values.html |
| Cross-reference geodatabase: DatasetMapping, FieldMapping, ValueMapping, AnnoSymbolIds, SDEOwner, Version and their columns | Help page | …/help/production/topographic-mapping/cross-reference-geodatabase.html |
| Mapping workbook: Dataset/Field/Value Mapping sheets, columns as the tables', case-sensitive names | Help page | …/help/production/topographic-mapping/cross-reference-mapping-files.html |
| CreateCrossReferenceGeodatabase's seven warnings; LoadData's map definitions, row-level error logging and "not found or is inaccessible" warnings | Tool pages | TP/create-cross-reference-geodatabase.html, TP/load-data.html |
| The geodatabase workbook's seven worksheets and their columns | Help page | …/help/production/topographic-mapping/geodatabase-excel-files.html |
| GenerateExcelFromGeodatabase (.xls or .xlsx), GenerateGeodatabaseFromExcel | Tool pages | TP/generate-excel-from-geodatabase.html, TP/generate-geodatabase-from-excel.html |
| SpreadsheetML (.xlsx) is ECMA-376 Office Open XML | ECMA-376 Part 1, 5th edition | ecma-international.org |
| MEF rules and worked examples for JOG, ONC, TPC and STANAG 3591 ed. 6; CalculateMEF's parameters and defaults (150 ft, 20 m, 20 m) | Four algorithm pages and the tool page | TP/how-maximum-elevation-figures-are-calculated-for-a-jog.html, TP/calculate-mef-onc.html, TP/calculate-mef-tpc.html, TP/how-maximum-elevation-figures-are-calculated-for-a-stanag.html, TP/calculate-max-elevation-figures.html |
| CalculateMagneticComponents' nine components, GRID_VARIATION needs LCC, the epoch of the date; GenerateIsogonicLines appends lines at base + interval | Tool pages | TP/calculate-magnetic-components.html, TP/generate-isogonic-lines.html |
| The WMM2015v2, WMM2020 and WMM2025 coefficient files and NOAA's WMM2025 test values; the model's equations | NOAA NCEI downloads (2026-10-31) and *The US/UK World Magnetic Model for 2025-2030: Technical Report* | https://www.ncei.noaa.gov/products/world-magnetic-model |
| Contour codes (`HQC` 1, 2, 3, 5, 6, 14, 22) and their meanings; GenerateTopographicContours' 24 parameters | Tool pages | TP/generate-topographic-contours.html, TP/identify-contours.html |
| Tops and depressions, closed contours, a point at every extreme cell, the 1:250,000 feet rule (*re-read*) | Tool page | TP/generate-spot-heights.html |
| ValidateSpotHeights' `IS_VALID` codes 0–4 | Tool page | TP/validate-spot-heights.html |
| ThinSpotHeights' parameters; invisibility; depression = enclosed by higher contours (*re-read*) | Tool page | TP/thin-spot-heights.html |
| ThinHydrologyLines: 0/1 invisibility, hierarchy 0 locks, min spacing along the parent, unsplit, angles (*re-read*) | Tool page | TP/thin-hydrology-lines.html |
| RemoveSmallLines: declination, smallest kept, combined length, overshoots, recursion, splitting (*re-read*) | Tool page | TP/remove-small-lines.html |
| RepairSelfIntersection: crossing not touching, equal z, loop length, SPLIT, REPAIR_ENDS for cul-de-sacs (*re-read*) | Tool page | TP/repair-self-intersections.html |
| IdentifyNarrowPolygons: split types 0–4, `split_length`, `FID`, taper toward connecting features (*re-read*) | Tool page | TP/identify-narrow-polygons.html |
| PolygonToCenterline: `FID`, `FID_<input>`, `FID_<connecting>`; split at junctions | Tool page | TP/polygon-to-centerline.html |
| Elevation bands: band counts and area shares cited from the TDS TM specification §3.3.6.3; `Low_Value`, `High_Value`, `TintBand`; JOG-A sample values | Tool pages | TP/generate-elevation-bands.html, TP/generate-elevation-bands-from-features.html, TP/generate-elevation-guide-features.html |
| Location diagram's eight outputs; TPC features used "for the location diagram", no output named; the 5×5 sizing not given (*re-read*) | Tool page | TP/generate-location-diagram-features.html |
| Adjoining sheets' outputs and the scale keywords' specifications | Tool page | TP/generate-adjoining-sheets-features.html |
| GeoNames: NGA GNS files, `UFI`/`UNI`, Matching Fields value table, duplicates (*re-read*) | Tool page | TP/geonames-to-geodatabase.html, TP/update-geonames.html |
| ExportTopology writes XML of classes, ranks, rules and tolerance for ImportTopology; no schema given (*re-read*) | Tool pages | TP/export-topology.html, TP/import-topology.html |
| GeodatabaseToShape's export modes and MGCP/MUVD naming; the shapefile format | Tool page; ESRI Shapefile Technical Description (1998) | TP/geodatabase-to-shape.html |
| ExtractDataByFeature's value table, filters, replica options | Tool page | TP/extract-data-by-feature.html |
| The refused pages (map, layout, portal, rule workbook, MGCP TRD, GAIT) | Tool pages | TP/apply-building-offsets.html … TP/gait.html (all 20) |
| Published methods the equivalents use | Blum 1967 (medial axis); Serra 1982 (morphology); Thomson & Richardson 1999 (strokes); Chaikin 1974; Horn 1981; Freeman & Shapira 1975; NGA.STND.0037 (MGRS) | as cited in the code |

No benchmark is at stake: these are batch tools on a sheet's data.

## 5. Decision

1. **Storage.** The toolbox's own documented tables live in the GeoPackage under their documented names: the six
   cross-reference tables in the GeoPackage CreateCrossReferenceGeodatabase writes; the FeatureLevelMetadata table and
   GeoNames table wherever the user made them. No `grapy_*` table is added: what the toolbox keeps beyond these is
   ADR-225's topology, ADR-228's subtypes and defaults and management's domains. Workbooks are ECMA-376 `.xlsx`, written
   and read with the standard library. The WMM coefficient files are bundled unchanged in
   `src/grapy/grapy/_topo_prod_data/` (public domain, NOAA NCEI / BGS).
2. **Built to their pages (16):** ApplyFeatureLevelMetadata, CalculateDefaultValues, CalculateMEF,
   CreateCrossReferenceGeodatabase, EliminatePolygon, ExtractDataByFeature (checkout replicas refused by name),
   GenerateExcelFromGeodatabase and GenerateGeodatabaseFromExcel (`.xls` refused by name), GeodatabaseToShape, LoadData,
   MergeLinesByPseudoNode, RemoveCutbackVertices, RepairSelfIntersection, SplitFeatures, **ExportTopology and
   ImportTopology** — the last two in grapy's own XML (§5.8 (h)): the page leaves the format open and asks only that
   ImportTopology read what ExportTopology wrote, which grapy's pair does.
3. **Equivalents (21),** each naming its method: CalculateMagneticComponents and GenerateIsogonicLines (the World
   Magnetic Model; Esri's WMM.xml refused by name), CalculateMetrics (minimum bounding rectangle, Horn aspect, MGRS;
   the Image City Map precision refused), FillGaps (morphological closing), GenerateAdjoiningSheetsFeatures (scale None;
   the specification scales refused), GenerateElevationBands, GenerateElevationBandsFromFeatures (ONC and TPC refused),
   GenerateElevationGuideFeatures, GenerateLocationDiagramFeatures (`tpc_features` refused), GenerateSpotHeights,
   GenerateTopographicContours (RESAMPLE_RASTER refused), GeoNamesToGeodatabase (a shapefile source refused),
   IdentifyContours, IdentifyNarrowPolygons, PolygonToCenterline, RemoveSmallLines, SetLineDirection,
   ThinHydrologyLines, ThinSpotHeights, UpdateGeoNames, ValidateSpotHeights.
4. **Refused, each for its own reason (18):** ApplyBuildingOffsets, ApplyMasksFromRules,
   ApplyVisualSpecificationToMap (an ArcGIS Pro map's layers and symbols; unpublished rule schemas),
   CalculateBridgeOffsets and MakeMasksFromRules (Pro's rendered symbols), ApplyElementValues, PopulateMapSheetInfo and
   GenerateProductLayout (a Pro layout), DownloadJobFiles, UploadJobFiles (a portal's Workflow Manager, no network,
   ADR-223 §5.3), UpdateTopographicProductionServiceDatabase (an enterprise server object extension's unpublished
   tables), MakeGridsAndGraticulesLayer (Esri's unpublished grid XML; a group layer), ExportGeneralizedData and
   ImportGeneralizationData (Esri's unpublished generalization rule workbook), ExportMetadata, ImportMetadata and
   UnzipCellAndImport (the MGCP/MUVD Technical Reference Documents, not published), GAIT (NGA's external program).
   The research had proposed refusing ExportTopology and ImportTopology too; on re-reading the page, which leaves the
   format open, they are built (point 2).
5. **Parameters not honoured are refused by name**, never ignored: `checkout_replica` and `replica_name`; `.xls`
   workbooks; `wmm_file` in XML; the `Image City Map` MGRS precision; the three specification scales of
   GenerateAdjoiningSheetsFeatures; `ONC` and `TPC` of GenerateElevationBandsFromFeatures; `RESAMPLE_RASTER`;
   `tpc_features`; a GeoNames shapefile; MGRS in the polar UPS areas.
6. **Units.** Linear and areal parameters take arcpy's unit words; a decimal-degree default on projected data is
   converted at 111,319.49 m per degree (the WGS 84 equator).
7. **Messages.** The documented warnings are written with the page's words where it gives them.
8. **INFERRED, for the owner's confirmation ([Q-221](../open-questions.md)):**
   - (a) storage: the cross-reference tables as GeoPackage attributes tables with `Id` an integer column beside the
     object ID, and text columns for everything else; the Version row holds `1`; the WMM files bundled as data;
   - (b) ApplyFeatureLevelMetadata: a null in the favourite is written as null; `MetadataFavoriteName`, `MetadataTokens`
     and the object ID are not applied; USER is the login in capitals, as editor tracking records it (ADR-240 §5.6 (b));
     a layer with no selection applies to all it shows;
   - (c) CalculateMEF: terrain (raster or points) is in metres; the three fields hold feet; ONC, TPC and STANAG take the
     larger of the obstruction and terrain branches, JOG chooses by its 150 ft test using `vo_allowance`; "vertical
     error" is `vo_accuracy` for obstructions and `terrain_accuracy` for terrain; a raster's maximum is that of cells
     whose centres lie in the cell; "Feet" is the international foot; the pages' rules win over their own examples;
   - (d) MergeLinesByPseudoNode: a pseudo node is an end shared by exactly two lines; FIRST is the default rule; the kept
     feature's direction is kept; USE_DEFAULTS writes the fields' own defaults;
   - (e) the magnetic tools: degrees, nanoteslas and degrees per year; the altitude is a height above the WGS 84
     ellipsoid; GRID_VARIATION is the declination less the grid convergence (east positive); the isogonic drift is the
     yearly change of declination at each line's midpoint, written `0.00` in a text field; `base` defaults to 0; the
     declination is sampled on a 64 × 64 grid over the extent;
   - (f) CalculateMetrics: lengths and areas in metres (a geographic class measured in its UTM zone); a polygon's length,
     width and orientation are its minimum-area rectangle's long side, short side and long side's azimuth (0–180°); a
     line's or polygon's elevation is its highest vertex, or highest raster value under a vertex; `in_precision` is decimal
     places; points take ANGLE_OF_ORIENTATION only with a raster (the downhill direction);
   - (g) PolygonToCenterline's and IdentifyNarrowPolygons' outputs key on `OBJECTID`, so the documented `FID` field can
     exist beside it in SQLite;
   - (h) a topology definition file is grapy's XML (`GrapyTopology`, `FeatureClass`, `Rule`; subtypes by name); a file
     of any other root is refused;
   - (i) the sheet diagrams: GenerateAdjoiningSheetsFeatures takes the area-of-interest layer's features meeting the
     buffered area as the sheets, ASG_AOI is the buffered area (CLIP_AOI: clipped to the sheets), and the coast area is
     the water within it; GenerateLocationDiagramFeatures' LD_AOI is five times the area of interest's width and height
     centred on it, the WAC/ONC/sheet classes are clipped to it, JOG_COAST_A is the land within it and JOG_COAST_L its
     shoreline, with the thinned hydrography (ONC, TPC) cut from the land and added to the shoreline;
   - (j) GenerateElevationBandsFromFeatures: the contour interval is the smallest difference between contour values; a
     face bounded by one contour lies on the side away from its neighbour's range; a face takes the band holding its
     range's midpoint; JOG-A uses the page's sample values in the contours' units; TintBand is the band's 1-based number
     (also for GenerateElevationBands);
   - (k) GenerateElevationGuideFeatures: an elevation range of 100 or less gets no band (the cited rule's 0); band
     polygons and holes under the minimum area are removed; EGB_BANDS_L is the band limits inside the area; narrow and
     short hydro areas are left out of EGB_HYDRO_A; spots are thinned highest first by the search distance;
   - (l) GenerateSpotHeights: a closed contour inside a lower one is a top and inside a higher one a depression; with no
     enclosing contour the raster's mean decides; at 1:250,000 and smaller the default z factor is 3.28084 and only then
     are values rounded up to 5 feet;
   - (m) contours: a closed contour is a depression when the mean of the raster inside it (inner closed contours' areas
     left out) is below its value; index contours are multiples of the index interval, five contour intervals when not
     given; QUARTER_AUXILIARY adds quarter and half levels; each scale's default interval (1:5,000 2 m … 1:1,000,000
     300 m, 1:25,000 10 m as the page says); raster smoothing is a Gaussian of σ = tolerance ÷ cell size; contours are
     clipped to the area of interest; the depression count counts from the innermost outward;
   - (n) RemoveSmallLines: SPLIT writes each split piece back as its own feature (the page's *Unsplit Line* note); a small
     line's declination is measured against each other line at its junction; the combined length follows two-line nodes;
   - (o) SetLineDirection: heights are the DEM cell under each vertex; a network's outlets are its end nodes within the
     tolerance of its lowest end node, and each line runs from the node farther (in lines) from an outlet; split flow
     cuts lines at interior peaks higher than both neighbours by more than the tolerance;
   - (p) ThinSpotHeights: invisibility 0 visible, 1 hidden; a spot marked in the high/low field, or else the highest and
     lowest, is always kept; then the extreme spot inside each innermost closed contour; then by descending elevation,
     each kept spot clearing its search distance; `max_spots` caps the visible spots;
   - (q) GeoNames: one point per distinct UFI at the first row's `lat_dd`/`long_dd`, one table row per name; fields
     matched by name without case or by the Matching Fields' source and target columns; a duplicate is an identical UFI
     and point (feature) or UNI (name); UpdateGeoNames keeps a stored name ID the GeoNames table holds, else takes the
     approved (`nt` N) name of lowest `name_rank`, writing its UNI and `full_name`;
   - (r) ValidateSpotHeights: the innermost closed contour holding a spot is its contour; codes tested 4, 3, 2, 1; a top's
     spot must be above its contour and below the next; code 1 when a raster cell inside the top is higher than the spot;
   - (s) workbooks: Booleans written as Boolean cells and read from TRUE/FALSE/1/0; field types as
     `esriFieldType*` (SmallInteger, Single and BigInteger included); domain field types Short, Integer, Float, Double,
     String, Date; a class with subtypes gets one Fields row per field per subtype; GenerateGeodatabaseFromExcel adds to
     an existing GeoPackage;
   - (t) GeodatabaseToShape: a subtype's applicable fields are those holding a value in its features or taking a domain in
     it; VALUES_AND_DESCRIPTIONS adds `<field>_D`; MGCP and MUVD read the feature code from `FCODE` or `F_CODE`;
   - (u) EliminatePolygon and FillGaps: ties go to the earlier input and then the lower object ID; the surrounding layers
     are read whole; a gap is filled only when it is no larger than the maximum (inclusive); IdentifyNarrowPolygons'
     narrow parts are those an opening by the minimum width removes, slivers under a quarter of its square staying wide;
     a taper is a triangle from the junction toward the wide part's centre or a connecting feature;
   - (v) ThinHydrologyLines: a stroke continues up the branch of lowest hierarchy, then the longest upstream length or
     (USE_ANGLES) the straightest; the outlet's stroke is never hidden for length; a hidden stroke hides the strokes
     flowing into it; spacing compares tributaries' junctions along the parent stroke;
   - (w) SplitFeatures and RepairSelfIntersection: every piece keeps all attributes; the first (longest) piece keeps the
     object ID.

## 6. Consequences

- **State.** Nothing in the catalogue and nothing on the server. At runtime, only the GeoPackage a tool works on: the
  six cross-reference tables in the file CreateCrossReferenceGeodatabase writes, and the user's own classes and tables
  the tools change; ExportTopology's file and the workbooks are files the tool is told to write. The WMM coefficient
  files are read-only package data, the same on every node.
- **Counts.** `arcpy.topographic` moves from 0 built and 55 refused as a whole module (a schedule) to 37 built — 16 to
  their pages and 21 equivalents — and 18 refused, none unexamined. All modules gain 37 built names.
- **Positive.** A topographic toolbox runs its data management (cross-reference loading, schema workbooks, extraction,
  shapefile delivery), its contour, spot height, MEF and magnetic computations, and its generalization tools on a
  server, from public sources.
- **Negative.** The equivalents' numbers differ from Pro's and each run says so. The cross-reference database and the
  topology file are not files Pro opens. A GeoPackage reader sees six tables named DatasetMapping … Version in the
  cross-reference file. 15 kB of coefficient files are carried in the package.
- **Ports created.** None. Standard library, numpy, scipy, shapely, pyproj and rasterio, all already grapy's.

## 7. Assumptions this decision rests on

- The pages' arithmetic and column lists are what Pro implements; where an example contradicts its own rule (§4, the
  JOG, ONC and TPC examples), the rule is.
- The World Magnetic Model's coefficient files are the model Esri's tools use for the date's epoch (the page says the
  five-year model of the date).
- MGCP and MUVD Technical Reference Documents are not publicly published (they are distributed to the programme's
  members); if they are, three refusals become buildable.

## 8. Dependencies

[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) §5.3 (no network, no external programs),
[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) (§5.4 refusal by name, §5.6 equivalents),
[ADR-225](ADR-225-grapy-holds-topology-and-relationship-classes.md) (topology, relationship classes),
[ADR-228](ADR-228-grapy-holds-subtypes-and-attachments.md) (subtypes, defaults),
[ADR-240](ADR-240-grapy-holds-editing-behaviours-and-registration.md) (the login as editor tracking records it).
Clean room (CLAUDE.md §5): built from the public pages in §4, NOAA's published model and the cited papers; no Esri
implementation or product file read.

## 9. Revisit triggers

- Esri publishes the generalization rule workbook layout, the grids and graticules XML, the topology definition XML or
  WMM.xml; or the MGCP/MUVD TRD becomes public.
- NOAA releases the next World Magnetic Model (WMM2030, due for 2030.0): its `.COF` is added to `_topo_prod_data`.
- A comparison with Pro (condition 2) shows an equivalent's answer differing in kind, not only in degree.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The owner answers [Q-221](../open-questions.md).**
2. **The built tools compared with ArcGIS Pro** on one small dataset: CalculateMEF (the four specifications),
   CalculateMagneticComponents, GenerateExcelFromGeodatabase's workbook, LoadData through a cross-reference, and
   IdentifyContours' codes — where the page fixes the answer, grapy's must match it.
3. **WMM2030 bundled** when NOAA publishes it, before 2030.0: a date from 2030.0 is an error by the
   five-year validity of WMM2025, as long as no later model is bundled. *(Read 2026-10-31: this is a date, not
   work. NOAA and BGS publish each World Magnetic Model near the end of the one before it, as WMM2025 came out in
   December 2024, so WMM2030 is not expected before late 2029, and nothing can be bundled until then. The
   behaviour until then is held by a test: `_topo_prod_wmm.model_for` refuses a date no bundled model covers, and
   `test_topographic_relief.test_the_model_follows_the_epoch` asserts that 2031.0 is refused; it passed on this
   branch, 21 tests, OK. Proposal for the owner: make this a standing trigger rather than a live condition, or
   move it to a debt row whose trigger is NOAA's publication.)*
