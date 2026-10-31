# ADR-230 — grapy holds mosaic datasets

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` for the model · `HIGH` for the methods and operators the pages define (FIRST, LAST, MIN, MAX, MEAN, SUM, ZOrder, None, Lock Raster, By Attribute), which the tests check by hand · `LOW` for agreement with Pro wherever the pages leave a rule open — the north-west position, the seamlines, blending, cell size ranges — which follow this server's image services instead |
| **Decided** | 2026-10-30, by owner decision on scope and order: mosaic datasets are their own ADR and batch, after subtypes and attachments (ADR-228) and LAS datasets and TINs (ADR-229), before the utility network ([ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.7). The storage and reading design below is INFERRED and listed for confirmation in §5.9 and [Q-198](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Before this decision thirty-five names in `arcpy.management` answered *"needs a data model this server does not hold
yet (ADR-224 group 2): mosaic datasets, catalog datasets and raster collections"*, and `arcpy.Raster` could not open a
mosaic dataset. A user tool that catalogues a folder of orthophotos, sets which is on top, and reads the result as one
raster could not run on this server — although the server's own image services have been mosaics with every mosaic
method and operator since ADR-152, ADR-158 and ADR-161.

What ArcGIS publishes, and what it does not:

- **The catalog is documented as a table.** The mosaic dataset attribute table page names its fields — ObjectID, Raster,
  Name, MinPS, MaxPS, LowPS, HighPS, Category (0 Unknown, 1 Primary, 2 Overview …), Tag, GroupName, ProductName,
  CenterX, CenterY, ZOrder — and what each means: *"Lower values are drawn on top"*, *"A null ZOrder value is treated
  as 0"*, MinPS and MaxPS *"determine which raster is used at a given display scale"*, LowPS and HighPS *"match when
  there are no pyramids"*.
- **The display rules are documented in words.** "Mosaicking rules for a mosaic dataset" orders North-West, Closest to
  Center, By Attribute and Closest to Nadir by ZOrder, then PixelSize, then the method's own criterion, and None by
  ObjectID; it defines FIRST, LAST, MIN, MAX, MEAN and SUM, and says of BLEND only that it *"relies on an algorithm that
  is weight based and dependent on the distance from the pixels to the edge"*. It does not define the north-west
  position, whether a finer or coarser image is on top, how seamlines are generated, or how NoData takes part.
- **Each tool's page gives its parameters and defaults**: SetMosaicDatasetProperties (its forty-odd properties),
  MakeMosaicLayer (its keywords, FIRST by default, *"LAST is not valid with SEAMLINE"*), CalculateCellSizeRanges (a 30 m
  image *"visible at a scale appropriate for 300 m"* with the default factor 10), BuildFootprints, BuildBoundary,
  RemoveRasters (*"You must specify a selection or a query; otherwise, the tool will not run"*) and the rest.
- **The storage is not documented**: a mosaic dataset's AMD tables inside a geodatabase, its raster type table, its
  overviews, its item cache (Cloud Raster Format) and a folder's `.amd` file are Esri's own.

Every page cited here was read on doc.esri.com (pro.arcgis.com's tool and help pages redirect there) on 2026-10-30; no
Esri implementation was read (CLAUDE.md §5).

## 2. Alternatives considered

### Alternative A — a catalog feature table in the GeoPackage, mosaicked by grapy on read (chosen)

A mosaic dataset is a polygon feature table named after it — the catalog, whose rows are the items, whose shapes are
the footprints and whose columns are the documented fields — beside grapy's own two tables for its properties and
boundary. Items are raster files read where they lie. `Raster(<mosaic>)` mosaics the items cell by cell in numpy, by
the method and operator, as the server's VRT mosaic reader does for an image service. **For:** the catalog is a table
the cursors, AddField and CalculateField already work on — a ZOrder or an acquisition date is set the way a script for
Pro sets it; every method and operator is computable; no Esri format is reproduced. **Against:** a mosaic dataset made
in Pro cannot be opened, and one made by grapy cannot be opened by Pro.

### Alternative B — a GDAL VRT built on read

**For:** GDAL composes a VRT natively, and the server's image services are VRTs. **Against:** a VRT draws its sources
last on top; it has no ZOrder, no distance order, no MIN, MAX, MEAN, SUM or BLEND, and no per-item footprint clip. The
server combines in its own reader for that reason (ADR-158); grapy does the same in numpy rather than through a VRT it
would have to post-process.

### Alternative C — read and write Esri's mosaic dataset tables

**For:** interchange with Pro. **Against:** the tables are undocumented, which the clean room forbids reproducing
(CLAUDE.md §5). Rejected.

### Alternative D — refuse mosaic datasets

Rejected by the owner's scope answer.

## 3. Counterarguments to the preferred option

**Where the pages are silent, grapy follows the server, not Pro.** The north-west position, the seamlines and BLEND's
weights are what this server's image services already draw (ADR-152, ADR-161, ADR-158) — not reproductions of Pro.
A tool's mosaic and the image service of the same rasters then agree, which is the property this project can test;
agreement with Pro on those three cannot be tested without Pro's internals.

**Items must share the mosaic's grid.** A mosaic dataset in Pro resamples and projects items on the fly. grapy reads an
item exactly when its cells line up with the mosaic's — the same cell size, or a whole multiple taken by NEAREST — and
refuses any other alignment, reference or resampling by name, as every other grapy raster tool does (ADR-224 §5.5).
Orthophotos tiled on one grid, the common case for a tool, read; a mixture of sensors does not (condition 4).

**No overviews.** A mosaic dataset is read at its items' own cell size; one too large for a run's memory fails by
ADR-223's limits.

## 4. Evidence

`src/grapy/tests/test_mosaic.py`, forty-three tests on rasters the tests write: two 4 × 4 rasters of 10 and 20 that
overlap in two columns, a 2 m raster beside them, a two-band raster, a raster with a hole. Each expected mosaic is
worked by hand in the test — the operators give 10, 20, 10, 20, 15 and 30 over the overlap; BLEND gives 13 and 17
where one item's cell is one in from its edge and the other's on it, (2·10 + 20)/3 and (10 + 2·20)/3; the seamline
falls at x = 3, halfway between the centres. No performance claim is made.

## 5. Decision

1. **grapy holds mosaic datasets, not catalog datasets.** A catalog dataset is a collection of layers, scene layers and
   portal items that no tool reads as data and a fenced tool mostly cannot reach (ADR-223 §5.3); its three tools are
   refused with that reason.
2. **Storage, INFERRED (§5.9 (a)).** In a GeoPackage — a mosaic dataset *"must be created in a geodatabase"*, and
   grapy's is a GeoPackage — three tables registered as the `grapy_mosaic` extension in `gpkg_extensions`: the catalog
   `<name>`, a polygon feature table with `OBJECTID`, the shape (footprint), `Raster` (the item's file, an absolute
   path) and the documented fields, to which a user may add fields; `grapy_mosaic` (reference, band count, pixel type,
   product definition, referenced, boundary, the properties as JSON under Describe's names); `grapy_mosaic_item` (each
   item's file time when last read, its NoData definition). An item is one raster file of the Raster Dataset type. A
   mosaic dataset is named `<file>.gpkg/<name>`, or by its name in a GeoPackage workspace; ListFeatureClasses does not
   list its catalog, ListDatasets lists it as a mosaic, Delete removes it and grapy's rows for it.
3. **Raster Dataset is the only raster type** *(and Frame Camera since 2026-10-30, added from a frames table —
   [ADR-234](ADR-234-grapy-holds-terrain-datasets-and-ortho-mapping.md) §5.3)*. The other raster types read a sensor's
   metadata through Esri's raster type definitions; they, product definitions, band definitions across items, CreateRasterType and
   AlterMosaicDatasetSchema are refused. Items must have the mosaic's band count and reference, and lie on its grid —
   the same cell size or a whole multiple read by NEAREST, corners whole cells apart (condition 4).
4. **Methods and operators.** Built as documented: None, North-West, Closest to Center, Closest to Nadir (CenterX and
   CenterY), Lock Raster, By Attribute (numbers and dates, the documented date formats), and FIRST, LAST, MIN, MAX, MEAN
   and SUM, after ZOrder and pixel size. Built as this server's image services draw them, the pages being silent: the
   north-west position is the north-west corner of the extent read, an image's centre its raster extent's centre
   (ADR-152); **Seamline** draws each item where its centre is nearest, the halfway lines (ADR-161) — so BuildSeamlines
   stores nothing and is refused, as are SEAMLINE geometry exports and imports; **BLEND** weighs a cell by its distance
   inside its item plus one, up to 64 (ADR-158), and says so when it runs. **Closest to Viewpoint is refused**: its
   viewpoint is the one a user moves in Pro's display.
5. **Not kept, refused with reasons**: overviews (BuildOverviews, DefineOverviews — their files' format is not named
   and their level count is "an optimal value"), pyramids and thumbnails for items, estimated statistics, the item
   cache (CRF) and pixel cache, mobile mosaic datasets, colour balancing, AnalyzeMosaicDataset (checks described in a
   sentence each, results only in Pro's pane), ComputeDirtyArea and ComputeMosaicCandidates (methods not documented),
   MergeMosaicDatasetItems and SplitMosaicDatasetItems (an item of several rasters is Esri's item raster), and
   GenerateRasterCollection (raster function templates).
6. **A mosaic dataset is a raster to every tool that reads one.** `arcpy.Raster`, `RasterToNumPyArray`, map algebra and
   the `sa` and `ia` tools open a mosaic dataset or a MakeMosaicLayer layer as one raster, mosaicked by its method;
   `Describe` answers the Mosaic Dataset properties with the Dataset and Raster Dataset groups and the catalog's fields.
7. **Names.** Built: `CreateMosaicDataset`, `CreateReferencedMosaicDataset`, `DeleteMosaicDataset`,
   `AddRastersToMosaicDataset`, `RemoveRastersFromMosaicDataset`, `SynchronizeMosaicDataset`,
   `RepairMosaicDatasetPaths`, `CalculateCellSizeRanges`, `BuildFootprints`, `BuildBoundary`,
   `SetMosaicDatasetProperties`, `MakeMosaicLayer`, `DefineMosaicDatasetNoData`, `ExportMosaicDatasetPaths`,
   `ExportMosaicDatasetItems`, `ExportMosaicDatasetGeometry`, `ImportMosaicDatasetGeometry`. Every other name of the old
   group-2 entry gives its own reason; `arcpy.ia.DetectControlPoints` and `TransformImageShapes` now name what they
   still lack — sensor orientations — rather than the mosaic dataset. *(Since ADR-234 grapy holds Frame Camera
   orientations, and the two name what they lack now: Esri's deep learning models, and the deep learning tools'
   undocumented iShape field.)*
8. **What a parameter's page leaves undefined is refused, not guessed**: BuildFootprints' `approx_num_vertices` other
   than -1 (how vertices are reduced is not said), `request_size`, `MAINTAIN_EDGES` and their tolerances;
   ExportMosaicDatasetItems' formats other than TIFF, image space and band selection; a `.dbf` paths table.
9. ~~**INFERRED, for confirmation**~~ **Confirmed 2026-10-31 by owner decision in Q-198** (*"Hepsini onayla"*) ([Q-198](../open-questions.md)):
   - (a) the storage of §5.2: the catalog as a GeoPackage feature table named after the mosaic dataset, with
     `OBJECTID` and `Raster` holding the file path; grapy's two tables; only in a GeoPackage; items read in place, and
     mosaicked by grapy rather than through a VRT;
   - (b) an added item's fields: the footprint is its raster's extent, LowPS the mean of its cell width and height,
     HighPS LowPS times its coarsest overview's factor, Category 1, Tag `Dataset`, CenterX and CenterY its extent's
     centre, ZOrder null; a raster with no reference takes the mosaic's; a duplicate is the same path;
     `OVERWRITE_DUPLICATES` keeps the item's OBJECTID; wildcards and paths match without regard to case, and `REGEX:`
     matches the whole path; `operation_description` is kept in the properties;
   - (c) the properties a new mosaic dataset starts with where the pages give none: North-West, FIRST, NEAREST, every
     method allowed, no limit on rasters per mosaic, a minimum contribution of one cell; the image service transmission
     properties unset;
   - (d) the read's extent is the boundary's envelope, or the items' with none; its grid is the finest item's; only
     Category 1 items are read; a broken item is left out with a warning;
   - (e) cell sizes: a mosaic is read at its cell size property or its finest item's; an item is in view when MinPS ≤
     cell size ≤ MaxPS, a null MinPS 0 and a null or zero MaxPS unbounded; CalculateCellSizeRanges sets MinPS 0 and
     MaxPS the group's nominal cell size times the factor, the nominal size the group's finest, the tolerance 0 when
     not given;
   - (f) order: after ZOrder, the finer item on top; then the method's criterion; then OBJECTID; the north-west
     position, image centres, the seamline and BLEND as the server's (§5.4); By Attribute dates as days, an item with
     no value last, DESCENDING the greatest difference first; Lock Raster in the order named; Seamline's ties by
     OBJECTID;
   - (g) combining: an item's NoData shows the items beneath while footprints may contain NoData, and decides the cell
     when they may not; MIN, MAX, MEAN, SUM and BLEND leave NoData out; MEAN and BLEND of integer cells round half to
     even in the mosaic's pixel type, a value it cannot hold refused; `clip_to_footprints` limits each item to its
     footprint only when set, as Describe's description reads; a coarser item is read by NEAREST;
   - (h) SynchronizeMosaicDataset's stale item is one whose file time changed, its "workspace" the folder of an item's
     raster without subfolders; RepairMosaicDatasetPaths replaces a leading path, whole folder names at a time without
     regard to case or slash, `*` moving every file into the new folder, the first matching pair applied;
   - (i) BuildFootprints: a cell is valid when any band holds a value in range, the footprint the union of valid
     cells' squares, a masked region a 4-connected group of invalid cells not touching the raster's edge,
     `min_region_size` 0 when not given; DefineMosaicDatasetNoData takes a band by number too and replaces an item's
     earlier definition;
   - (j) export and import fields the pages do not name: ExportMosaicDatasetPaths' `Path` and `Type`;
     ExportMosaicDatasetGeometry's `SourceOID` with the catalog's fields for footprints, none for the boundary,
     `LowPS`, `MinPS` and `MaxPS` for levels; ExportMosaicDatasetItems clipping whole cells and writing the NoData value
     given as the output's; ImportMosaicDatasetGeometry joining by object id when asked, uniting several matches, and
     setting the boundary to the union of the matched features;
   - (k) CreateReferencedMosaicDataset keeps the source's properties, fields and OBJECTIDs, holds `pixelSize` as the
     greatest MaxPS, and refuses a folder output (Esri's `.amd`); DeleteMosaicDataset keeps the item files; Describe's
     `format` is empty and `childrenNames` Footprint and Boundary.

## 6. Consequences

- **State.** In the GeoPackage the tool works on; items are the raster files in its run directory. Nothing in the
  catalogue or the server.
- **No new dependency**: rasterio, numpy, scipy and shapely, already in the image.
- **Names.** `tools/grapy-refusals.json` before → after: `arcpy.management` built 173 → 190, refused 274 → 257; all
  modules built 901 → 918, refused 1149 → 1132, unexamined 0 → 0.
- ~~**One difference from the server remains**: grapy orders by ZOrder and pixel size before the method, as the
  mosaicking rules page says; the server's `mosaicRule` orders by the method alone (condition 5).~~ None remains since
  2026-10-30: the server has a real ZOrder and orders by it first (condition 5).

## 7. Assumptions this decision rests on

- A user tool's mosaic fits in its run's memory: the mosaic is read whole. Nothing has been measured; a mosaic too
  large fails by ADR-223's limits.
- The server's interpretations in ADR-152, ADR-158 and ADR-161 are the ones to share. If one changes, grapy's changes
  with it.

## 8. Dependencies

[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.5–5.7,
[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) (the fence and limits),
[ADR-225](ADR-225-grapy-holds-topology-and-relationship-classes.md) (the `gpkg_extensions` registration it reuses),
[ADR-152](ADR-152-a-mosaic-is-a-catalog.md), [ADR-158](ADR-158-a-mosaic-combines-and-is-reordered.md),
[ADR-161](ADR-161-a-seamline-is-the-halfway-line.md) (the server's readings it shares); rasterio, numpy, scipy,
shapely; clean room (CLAUDE.md §5): only documented behaviour is reproduced, and no Esri file format.

## 9. Revisit triggers

- A user needs items on different grids or references — resampling and projecting on the fly are then the step
  (condition 4).
- A user needs a mosaic dataset larger than a run's memory — reading by window, or overviews, become the step.
- The server's mosaic rule changes how it orders or combines.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The model and its reading**: CreateMosaicDataset, AddRastersToMosaicDataset, Raster, RasterToNumPyArray, map
   algebra, Describe and ListDatasets on a mosaic dataset; the None, North-West, Closest to Center, Closest to Nadir,
   Lock Raster, By Attribute and Seamline methods and the seven operators, against hand-worked mosaics.
   **DISCHARGED 2026-10-30**: `test_mosaic.py` checks each method and operator over the overlap of two rasters — ZOrder
   before the method, By Attribute on numbers and dates and in both orders, the seamline at x = 3, BLEND's 13 and 17,
   MEAN's half to even — and a 2 m raster read by NEAREST beside them.
2. **The catalog tools**: RemoveRasters, SynchronizeMosaicDataset, RepairMosaicDatasetPaths, CalculateCellSizeRanges,
   BuildFootprints, BuildBoundary, DefineMosaicDatasetNoData, SetMosaicDatasetProperties, MakeMosaicLayer, the three
   exports, ImportMosaicDatasetGeometry, CreateReferencedMosaicDataset and DeleteMosaicDataset. **DISCHARGED
   2026-10-30**: footprints of 12, 8, 9, 16, 24 and 25 m² by radiometry, range, shrinking and region size; boundaries of
   28, 24 and 16 m² by union, append and overwrite; cell size ranges of 5, 10 and 20 and their grouping; broken paths
   exported, repaired by prefix and by `*`, and removed; stale and new items synchronized; items exported whole and
   clipped; footprint and boundary imports changing the mosaic read.
3. **Every name of the old group-2 entry is built or refused with its own reason.** **DISCHARGED 2026-10-30**: seventeen
   built, eighteen refused each with its reason (none answers the group-2 message or *not built yet*), checked by
   `tools/grapy_refusals.py --check` and `test_undocumented_methods_operators_and_formats_are_refused_by_name`.
4. **Items on different grids**: resampling (BILINEAR, CUBIC, MAJORITY, and reading coarser than an item), unaligned
   items and items in another reference are built to a documented method, or refused for good with the reason
   recorded.
5. **One ordering for the server and grapy**: the server's `mosaicRule` orders North-West, Closest to Center, By
   Attribute and Closest to Nadir by ZOrder and pixel size first, as the mosaicking rules page says and grapy does — or
   the owner decides the server's order stands and grapy's follows it. **DISCHARGED 2026-10-30, by owner decision (*"Gerçek ZOrder ekle"*).**
   The premise was checked first: the server had no ZOrder at all — its catalogue showed each image's place in the mosaic
   under that name, a different number for every image, so ordering by it first would have ended every method. The
   owner chose a real one. An image's ZOrder is now its own, null until set, kept in the catalogue's JSON beside its name
   and date (no migration), shown as itself in the catalogue, set or cleared through ArcGIS's `update` with
   `attributes={"ZOrder":n}` and the admin image update, and kept through every other edit of the catalogue. It comes
   before every method but Lock Raster — a lower value on top, a null one as 0, then the finer cell — and in the plain
   view without a rule too, before the order Move left. A service whose images carry none draws exactly as before.
   `MosaicRuleTests` checks the order by hand; `An_images_zorder_comes_before_the_mosaic_method_and_is_its_own` checks
   it end to end, with and without a rule, through a rename and back to null. Studio has no control for it yet:
   [D-326](../architecture-debt.md).
6. **The owner confirms or changes §5.9** ([Q-198](../open-questions.md)).
