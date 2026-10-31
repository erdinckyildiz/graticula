# ADR-234 — grapy holds terrain datasets and ortho mapping

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the window size pyramids, the TIN measurements at any level and the collinearity and RPC models, which are exact · `MEDIUM` for the z-tolerance pyramid, the matchers and the bundle adjustment, which are equivalents · `LOW` for agreement with Pro on the frame camera's angle, polarity and distortion conventions, which the public pages do not give |
| **Decided** | 2026-10-30, by owner decision: *"Yap"* — grapy gets terrain datasets and ortho mapping, after ADR-229 (TINs and LAS) and ADR-230 (mosaic datasets), which they build on. This reverses ADR-229 §5.6, which refused terrains. The storage below and the conventions §5.9 lists are INFERRED and listed for confirmation in [Q-202](../open-questions.md). |
| **Supersedes** | ADR-229 §5.6 (terrain datasets are refused) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Before this decision `arcpy.ddd` refused seventeen terrain names with *"a terrain dataset is a multiresolution pyramid
kept inside a geodatabase feature dataset, which grapy does not hold"* (ADR-229 §5.6), and `arcpy.management` refused
eighteen ortho mapping and stereo names with *"needs a data model this server does not hold yet: ortho mapping and
stereo workspaces"*. `arcpy.ia.Geometric`, `DetectControlPoints` and `TransformImageShapes` gave reasons that named the
same missing sensor models. A user tool that keeps a lidar ground model as a terrain, or that orients a block of drone
photographs and orthorectifies it, could not run on this server.

What the public pages define, and what they do not (read on doc.esri.com, which pro.arcgis.com's pages redirect to, on
2026-10-30; no Esri implementation was read, CLAUDE.md §5):

- **Terrain tools are documented by their parameters and their effects**: CreateTerrain's settings (average point
  spacing, WINDOWSIZE or ZTOLERANCE, the window size methods ZMIN, ZMAX, ZMEAN — "closest to mean" — and ZMINMAX),
  the pyramid level definition ("20 24000", a resolution and a reference scale), Add Feature Class To Terrain's value
  table, which tools invalidate a terrain and which do not. *How Add Terrain Pyramid Level works* says a z-tolerance
  level keeps "only the data necessary to achieve an approximate vertical tolerance" and that the z-tolerance "specifies
  the maximum deviation that can occur from the elevation of the terrain at full resolution" — a bound, not an
  algorithm. Secondary thinning is named (MILD, MODERATE, STRONG) with what each suits, not which points it removes.
  **Esri's pyramid storage is not published.** Add Terrain Points' page answers 404.
- **The frames and cameras tables are documented field by field**: perspective centre, Omega, Phi and Kappa in
  degrees, focal length, principal point and pixel size in microns, the affine and the distortion terms. **The pages
  give no rotation order, sign convention, matrix layout or distortion formula** — only that AngleDirection −1 is
  "clockwise" and Polarity −1 "opposite".
- **The ortho mapping tools are documented by their parameters**: Compute Tie Points names SIFT for LOW location
  accuracy and Harris for MEDIUM and HIGH, without saying how matches are made; Compute Block Adjustment names its
  options (CalibrateF, CalibratePP, the a priori accuracies …) without naming its estimator; neither names the fields of
  the tables it writes. Generate Point Cloud says repeated runs differ "because of random sampling". Compute Depth Map
  writes Esri's own `.dm` files. Detect Control Points runs "two deep learning models trained by Esri". Update Interior
  Orientation's page answers 404.
- **Public standards define the sensor models themselves**: the collinearity equations of a frame camera (Wolf, Dewitt
  and Wilkinson, *Elements of Photogrammetry*, 4th ed., ch. 11 and App. C; Mikhail, Bethel and McGlone,
  *Introduction to Modern Photogrammetry*), rational polynomial coefficients (RPC00B, NITF STDI-0002; OGC 06-042),
  and bundle adjustment (Triggs, McLauchlan, Hartley and Fitzgibbon, *Bundle Adjustment — A Modern Synthesis*, 2000).

## 2. Alternatives considered

### Alternative A — grapy's own terrain and sensor-model tables in the GeoPackage, standard methods where the pages are silent (chosen)

A terrain is grapy's tables beside its feature dataset in the GeoPackage, as ADR-225 to ADR-231 hold their models; a
level's surface is a TIN of ADR-229 triangulated from that level's points. Frame Camera items are added to ADR-230's
mosaic datasets from a frames table, their orientation kept in a grapy table. The sensor models are the public
standards; the matchers and the adjustment are the literature's methods, built as equivalents (ADR-224 §5.6). **For:**
no new dependency (numpy, scipy, shapely, rasterio, pyproj are in the image), no Esri format reproduced, every result
checkable against a hand-worked or synthetic answer. **Against:** a terrain or ortho mapping workspace made in Pro
cannot be opened, and one made by grapy cannot be opened by Pro.

### Alternative B — read and write Esri's terrain pyramids and ortho mapping workspaces

**For:** interchange. **Against:** both are unpublished; reading them means reverse-engineering Esri's files, which the
clean room forbids (CLAUDE.md §5). Rejected.

### Alternative C — OpenCV for matching, an existing bundle adjuster (Ceres, OpenMVG, COLMAP) for the adjustment

**For:** mature, fast, SIFT included. **Against:** each is a large native dependency for what scipy and numpy do at a
user tool's scale (ADR-223's limits); COLMAP and OpenMVG are whole structure-from-motion pipelines, and Ceres is a C++
library with no wheel the image carries. OpenCV's SIFT would let LOW location accuracy be built; that is a revisit
trigger (§9), not this decision.

### Alternative D — keep both refused

Rejected by the owner's decision.

## 3. Counterarguments to the preferred option

**The z-tolerance pyramid will not keep Pro's points.** True: the page gives the bound and grapy meets it exactly, by
greedy insertion (Garland and Heckbert 1995; Heller 1990), which is not necessarily Pro's selection. A tool that counts
a level's points will see a different number; a tool that measures heights on a level will see heights within the same
tolerance of full resolution. The run says so in its messages.

**The frame camera conventions are guesses until a Pro comparison.** True, and the most consequential guess here.
Wolf's rotation is the one every photogrammetry text uses, so a frames table written for any other package reads the
same way under it — but whether Pro's default AngleDirection −1 means Wolf's angles or their negatives is not published.
A wrong guess shows at once (footprints in the wrong place), not silently; condition 5 measures it.

**An equivalent adjustment may not converge where Pro's does.** Possible on large or weak blocks; grapy uses
scipy's trust-region least squares with the problem's sparsity, the method Triggs et al. recommend, and fails by name
rather than answer an unconverged block.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A plane stays a plane at every level of either pyramid | TerrainToRaster at levels 0, 1 and 4 of z = x + 2y equals the plane at every cell; SurfaceVolume below z = 100 is 8500 and the 3D area 100√6 at level 4 | `test_terrain.py` |
| Window size thinning keeps what each method selects | 16 hand-set points, four windows of 2: ZMIN, ZMAX, ZMEAN, ZMINMAX keep exactly the hand-picked points; levels nest | `test_terrain.py` |
| Z-tolerance meets the page's bound | 300 points on a rough surface: every one within 0.5 and 2 of the levels' surfaces; a 3 m bump kept at tolerance 1 and dropped at 5; on a plane only the hull's four corners | `test_terrain.py` |
| Surface tools read a terrain level exactly | AddSurfaceInformation on z = x + 2y: line length √128, slopes 100 %, mean 7; polygon area 4√6, mean 9, slope 100√5 % | `test_terrain.py` |
| Collinearity is Wolf's | a nadir photograph from 25 m with f = 5 mm puts (1, 2, 0) at pixel (120, 60); the rotation is orthonormal and its terms are Wolf's eq. C-33 | `test_ortho.py` |
| Tie points land where the true orientation projects them | four synthetic photographs of a textured plane: every tie observation within 0.5 pixel of its true projection | `test_ortho.py` |
| The bundle adjustment recovers the orientation | frames off by up to 0.3 m and 0.12°: after adjustment every image projects a 10 × 10 ground grid within 0.5 pixel of the truth (it missed by more than 3), GCP RMSE under 2 cm; the ortho's error against the ground falls by more than half | `test_ortho.py` |
| The RPC model is RPC00B's | a linear RPC file orthorectified at constant height takes each cell from the column the closed form gives | `test_ortho.py` |

No performance claim is made: a user tool runs under ADR-223's limits, and a block too large for them fails by those
limits.

## 5. Decision

1. **grapy holds terrain datasets and the ortho mapping of Frame Camera mosaic datasets.** ADR-229 §5.6 is reversed.
2. **Storage, INFERRED (§5.9 (a)).**
   - A terrain is six tables registered as the `grapy_terrain` extension in `gpkg_extensions`: `grapy_terrain` (name,
     feature dataset, reference, CreateTerrain's settings, built, extent), `grapy_terrain_level` (resolution, reference
     scale), `grapy_terrain_source` (the feature classes taking part, with their value table columns),
     `grapy_terrain_point` (embedded points), `grapy_terrain_pyramid` (each level's points as Build Terrain thinned
     them; level 0 is full resolution) and `grapy_terrain_feature` (the lines and polygons as Build Terrain read them).
     A terrain path is `<file>.gpkg/<feature dataset>/<terrain>`; it must be created in a feature dataset, and its
     feature classes must lie in it, as documented.
   - A Frame Camera item's orientation is a row of `grapy_ortho_item` (the `grapy_ortho` extension): the model as JSON
     and the one it was added with, for Apply Block Adjustment's RESET. Stereo pairs are a feature table
     `<mosaic>_Stereo`. Control points are an ordinary point feature class with grapy's fields (§5.9 (h)).
3. **Raster types.** AddRastersToMosaicDataset gains the **Frame Camera** type: its input is a frames table (a
   GeoPackage table or a CSV file), a cameras table may be given as `aux_inputs` `CameraFile <table>`, and a frame's
   own camera fields take precedence, as the frames table page says. The Matrix orientation, ApplyECC, a Local Tangent
   Plane, a distortion table, AffineDirection −1 and a film coordinate system other than 1 without an affine are
   refused, each because its page gives the field without its formula. A mosaic dataset of Frame Camera items is not
   read as a raster on the fly (§5.5); CreateOrthoCorrectedRasterDataset writes its ortho.
4. **Built to their pages** (each INFERRED choice in the tool's docstring and §5.9): `arcpy.ddd` — CreateTerrain,
   AddTerrainPyramidLevel, RemoveTerrainPyramidLevel, ChangeTerrainReferenceScale, AddFeatureClassToTerrain,
   RemoveFeatureClassFromTerrain, ChangeTerrainResolutionBounds, BuildTerrain (window size as documented, z-tolerance
   as the equivalent below), AppendTerrainPoints, ReplaceTerrainPoints, DeleteTerrainPoints, RemoveTerrainPoints,
   TerrainToTin, TerrainToPoints, TerrainToRaster, and Describe of a terrain; every surface tool that takes a terrain
   reads it at its `pyramid_level_resolution` — SurfaceVolume, PolygonVolume, SurfaceContour, SurfaceSlope,
   SurfaceAspect, InterpolateShape, UpdateFeatureZ — and **AddSurfaceInformation is built on TINs, terrains and LAS
   datasets** with every property its page defines. `arcpy.management` — AppendControlPoints, AnalyzeControlPoints,
   ApplyBlockAdjustment, GenerateBlockAdjustmentReport (HTML, JSON, BRIEF JSON), ExportFrameAndCameraParameters (the
   Esri frames and camera table CSV), BuildStereoModel and CreateOrthoCorrectedRasterDataset (frame and RPC, constant
   elevation or DEM).
5. **Refused, each with its own reason** — `arcpy.ddd`: AddTerrainPoints (its page answers 404), BIMToTerrain (a BIM
   file workspace, Civil 3D's drawing format being Autodesk's); secondary thinning other than NONE and
   CONSTRAINED_DELAUNAY on CreateTerrain. `arcpy.management`: ComputeCameraModel (its EXIF estimation, refinement, DSM
   and flight path are named, not described), ComputeDepthMap and RemoveDepthMap (Esri's `.dm` files), ComputeFiducials
   (no detection method, no output fields, and its only reader has no page), UpdateInteriorOrientation (404),
   GeneratePointCloud (random sampling, ETM and MVM undescribed), GenerateExcludeArea (its page contradicts itself);
   ComputeTiePoints' and ComputeControlPoints' LOW location accuracy (SIFT); ComputeBlockAdjustment's POLYORDER0/1 (a
   polynomial moves an item off its grid, ADR-230 §5.3) and RPC (an RPC image enters a mosaic through a satellite
   raster type); CalibrateP and CalibrateK (the distortion terms' sign convention is not given), ComputeAntennaOffset,
   ComputeShift, rigCamera; PDF reports; PIX4D export; the Geoid correction (no EGM96 grid in the image). `arcpy.ia`:
   Geometric (its Polynomial or Projective approximation of the sensor model is not defined), DetectControlPoints
   (Esri's deep learning models), TransformImageShapes (the deep learning tools' undocumented iShape field) — *the last built since [ADR-248](ADR-248-grapy-carries-deep-learning-on-pytorch.md) §5.6, on the iShape grapy's own detector writes in pixel space.*
6. **Equivalents (ADR-224 §5.6)**, marked, counted apart and announced in their runs:
   - **BuildTerrain's z-tolerance levels**: greedy insertion to the vertical tolerance (Garland and Heckbert 1995;
     Heller 1990) from the convex hull and the anchor points, measured against full resolution. BuildTerrain is counted
     as built — its window size path is the page's — and its z-tolerance path says it is an equivalent when it runs.
   - **ComputeTiePoints, ComputeControlPoints, MatchControlPoints**: Harris corners (Harris and Stephens 1988) matched by
     normalised cross-correlation (Lewis 1995) of both images resampled on a common ground grid — object-space
     correlation (Helava 1988).
   - **ComputeBlockAdjustment (Frame)**: a bundle adjustment of the collinearity equations by sparse trust-region
     Levenberg–Marquardt least squares (Triggs et al. 2000; Wolf et al. ch. 17), with observations dropped past the
     maximum residual and solved again, CalibrateF and CalibratePP as camera unknowns, the a priori accuracies as
     weighted observations of the exterior orientation, and a DEM's heights as observations of the tie points.
7. **The sensor models are the public standards**: Wolf's collinearity (M = M(κ) M(φ) M(ω), eqs. 11-1, 11-2, C-33) and
   RPC00B's twenty cubic terms in their order. Brown's radial and tangential distortion is applied with the cameras
   table's coefficients in millimetres.
8. **Stale reasons are corrected** (ADR-229 left them): eight `arcpy.management` names and six `arcpy.conversion` names
   still listed "LAS datasets, TINs" among the models grapy lacks. Each now names what it does lack — multipatch and 3D
   object features (Add3DFormats, Remove3DFormats, Export3DObjects, Import3DObjects, GenerateLevelOfDetail,
   MultipatchToCollada, MultipatchToRaster), voxels (CreateVoxelSceneLayerContent), scene layers (MakeSceneLayer,
   PointCloudToRaster's point cloud scene layer, MeshToLAS's integrated mesh), BIM file workspaces (MakeBuildingLayer,
   BIMFileToGeodatabase, ExtractBIMFileFloorplan). None changed its decision. *(The multipatch, 3D object and voxel
   reasons were made false in their turn by ADR-236 and ADR-238 and replaced on 2026-10-31: [ADR-238](ADR-238-grapy-holds-multipatch-features.md) §12.)*
9. ~~**INFERRED, for confirmation**~~ **Confirmed 2026-10-31 by owner decision in Q-202** (*"Hepsini onayla"*) ([Q-202](../open-questions.md)):
   - (a) the storage of §5.2; the frame camera conventions — pixel coordinates from the image's upper-left corner, a
     pixel's centre at +0.5; without an affine, film x = (column − columns/2) × pixel size and film y = (rows/2 − row) ×
     pixel size from the fiducial centre (FCS 1); the default AngleDirection −1 takes the angles as Wolf's and +1
     negates them; the default Polarity −1 is Wolf's positive image and +1 mirrors it; the affine A0..B2 maps column
     and row to film x and y; Radial K0;K1;K2;K3 and Tangential P1;P2 are Brown's terms in millimetres, added to ideal
     film coordinates; RPC line 0, sample 0 at the upper-left pixel's centre; *(Pro 3.7 measured 2026-10-31, condition
     5: the film map without an affine, AngleDirection and the radial terms held; **two were changed to Pro's** — the
     affine's column and row count from the pixel's centre, half a pixel from the film map without one, and the y term
     2·P1·x·y takes x already distorted; Pro builds no item with Polarity +1, so the mirror stays unmeasured)*
   - (b) a Frame Camera item: a relative Raster path read from the frames table's folder; the cameras table as
     `aux_inputs` `CameraFile <table>`; footprint, LowPS and HighPS at the camera's AverageZ (0 when not given);
     CenterX and CenterY the perspective centre (its nadir); *(Pro 3.7 measured 2026-10-31, condition 5: **changed to
     Pro's** — CenterX and CenterY are the footprint's centroid, and LowPS and HighPS the footprint's width across x
     over the image's columns; Pro refuses an item without AverageZ, "Missing elevation", and reads 0 as missing)*
   - (c) terrain sources: a blank resolution bound is open; an embedded class without an embed name is
     `<name>_embed`; embedding and anchoring only for point and multipoint classes; embed fields refused; anchor points
     kept at every level; a bound change and a line or polygon class's removal leave a terrain built, a mass point
     class's removal invalidates it;
   - (d) pyramids: windows are squares from the extent's lower-left corner *(Pro 3.7 measured 2026-10-31, condition 4: **changed to Pro's** — from the corner of each tile of `round(spacing × √200000)` from the XY domain's origin, centred on the data on an axis whose tile index reaches 23,170; the extent's corner only where grapy does not know the domain, which is every coordinate system but the UTM zones)*; of ties, the earlier point; of two points at
     one x,y the later source's kept; window size levels thinned from the level before (as the page says), z-tolerance
     levels each from full resolution (so the documented bound holds at every level); *(Pro 3.7 measured 2026-10-31,
     condition 4: **changed to Pro's** — a window size level chooses in each window from full resolution and keeps
     every coarser level's points, built coarsest first, which is the page's sentence for ZMIN, ZMAX and ZMINMAX on
     nested windows and not for ZMEAN or for windows that do not nest; ZMEAN's ties are distances within 1e-9; a window
     is at least twice the average point spacing (ERROR 050073). Pro's windows do not start at the extent's corner but
     at one that follows the feature dataset's XY domain and the point spacing, which its outputs do not pin down —
     [D-339](../architecture-debt.md))* the extent grows with the data
     unless UPDATE_EXTENT; an invalidated terrain is not read until built; a level is named by its exact resolution;
     TerrainToPoints' MULTIPOINT writes one multipoint per source, no attributes; ReplaceTerrainPoints within an area
     replaces only the area's points; a point on an area's edge is inside it; `max_overview_size` kept and reported, no
     overview built;
   - (e) Describe of a terrain: `pyramidType`, `windowSizeMethod`, `secondaryThinningMethod`,
     `secondaryThinningThreshold`, `averagePointSpacing`, `maxOverviewSize`, `pyramidLevels`, `dataSources`, `isBuilt`,
     named after CreateTerrain's parameters (the Terrain properties page answers 404);
   - (f) AddSurfaceInformation on a triangulated surface: fields named after the keywords; a polygon's triangle midpoint
     its clipped piece's centroid; its AVG_SLOPE weighted by 3D area; a feature off the data area gets empty values;
   - (g) the matchers: images read as the mean of their bands; similarity thresholds LOW 0.6, MEDIUM 0.7, HIGH 0.8;
     4, 8 or 16 cells across an overlap for LOW, MEDIUM, HIGH density, one point per cell for REGULAR and as many
     strongest corners for RANDOM (deterministic); a 15 × 15 template at the coarser image's ground sample distance; a
     search of ±12 cells (MEDIUM) or ±4 (HIGH); the defaults MEDIUM, MEDIUM, RANDOM, MEDIUM; a control point's z the
     item's AverageZ;
   - (h) the control point feature class: one row per image observation — `PointID`, `Type` (1 tie, 2 GCP, 3 check),
     `Status`, `ImageID`, `ImageX`, `ImageY` (pixels), `XYAccuracy`, `ZAccuracy`, `Tag` — its shape the ground point,
     the names following MatchControlPoints' JSON; a ground-only GCP row has no ImageID; AppendControlPoints renumbers
     after the master and takes GCPSET's tie points as those sharing an image with a GCP; the JSON's points projected
     from its spatial reference (WGS 84 by default, as documented);
   - (i) AnalyzeControlPoints' fields and measures (`Coverage` the points' convex hull as a percent of the footprint;
     `ImageIDs`, `ImageCount`, `PointCount`, `Area`), `minimum_area` a percent of the smallest image, `maximum_level` 2;
   - (j) the adjustment: the solution table's fields (`ImageID`, `CameraID`, `PerspectiveX/Y/Z`, `Omega`, `Phi`, `Kappa`,
     `FocalLength`, `PrincipalX`, `PrincipalY`, their posterior standard deviations, `RMSE`, `Observations`) and the
     solution points as 3D points (`PointID`, `Type`, `Status`, `ImageCount`, `Residual`), the page saying polygons;
     image measurements σ 1 pixel; a priori accuracies 10 map units and 1° where neither the options nor the frames
     table give one; GCP accuracy 0.1 and a DEM's 1 when not given; REACTIVATE changes nothing, grapy dropping no image;
   - (k) the report's sections and keys (`summary`, `images`, `controlPoints`), its accuracy measured with the solution
     table's orientation; BRIEF JSON the summary alone;
   - (l) BuildStereoModel: the intersection angle between the two rays to the overlap's centroid at AverageZ; the
     overlap the smaller of its shares of the two footprints; a flight line the frames table's StripName; the table's
     fields and order;
   - (m) CreateOrthoCorrectedRasterDataset: each cell from the item whose perspective centre is nearest (Closest to
     Nadir); the cell size environment or the finest ground sample distance; the extent the footprints; NEAREST, or
     BILINEAR by the resampling method environment; an RPC ortho in WGS 84.

## 6. Consequences

- **State.** In the GeoPackage the tool works on — the terrain's tables beside its feature dataset, the Frame Camera
  orientations beside the mosaic dataset — and in the image files, frames tables and reports in its run directory.
  Nothing in the catalogue or the server.

**Positive.** A user tool can keep a multiresolution surface and measure it at any level, and can orient and
orthorectify a block of frame photographs, on this server, by arcpy's names. No new wheel.

**Negative.** Neither model interchanges with Pro. Five conventions of the frame camera are inferred (§5.9 (a)) and
could be wrong in a way only a Pro comparison shows. The matchers are slower than OpenCV's and have no SIFT.

**Names.** `tools/grapy-refusals.json` before → after: `arcpy.ddd` built 70 → 85 (equivalents 7 → 7), refused
86 → 71; `arcpy.management` built 192 → 203 (equivalents 11 → 15), refused 255 → 244; all modules built 968 → 994
(equivalents 84 → 88),
refused 1206 → 1180, unexamined 0 → 0. Fourteen reasons in `arcpy.management` and `arcpy.conversion` and three in
`arcpy.ia` reworded, no decision changed.

**Ports created.** None: rasterio reads RPCs and images behind grapy's own `_ortho` module, as it already reads rasters.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A user tool's block and terrain fit in its run's memory: images are read whole and the adjustment's Jacobian is held sparse; nothing has been measured, and a block too large fails by ADR-223's limits | Unverified |
| — | Wolf's rotation convention is the one frames tables written for other packages use | Holds for Pro 3.7: measured 2026-10-31 (condition 5), AngleDirection −1 and +1 within 0.002 pixel |

## 8. Dependencies

**Depends on**: [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.6–5.7,
[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) (the fence and limits),
[ADR-229](ADR-229-grapy-holds-las-datasets-and-tins.md) (a terrain's levels are its TINs),
[ADR-230](ADR-230-grapy-holds-mosaic-datasets.md) (Frame Camera items are its mosaic datasets' items),
[ADR-225](ADR-225-grapy-holds-topology-and-relationship-classes.md) (feature datasets and the `gpkg_extensions`
registration); clean room (CLAUDE.md §5): only documented behaviour and public standards are reproduced.

**Depended on by**: —

## 9. Revisit triggers

- A permissively licensed SIFT (OpenCV's, since the patent's expiry) enters the image: LOW location accuracy becomes
  buildable.
- A Pro comparison of a frame block (condition 5) contradicts §5.9 (a).
- A permissive constrained triangulator (ADR-229 condition 6): CONSTRAINED_DELAUNAY terrains become buildable.
- The Update Interior Orientation or Add Terrain Points page becomes readable.

## 10. Dissent

None recorded.

## 11. Conditions

1. **Terrain datasets are built and answer hand-worked values**: the terrain tools, Describe, and the surface tools at
   every level. **DISCHARGED 2026-10-30**: `test_terrain.py`, nineteen tests on the plane z = x + 2y, hand-set windows, a
   bump and a 300-point rough surface (§4).
2. **Ortho mapping is built and recovers a known orientation**: Frame Camera items, tie and control points, the bundle
   adjustment, apply and reset, the report, export, stereo pairs, and frame and RPC orthorectification.
   **DISCHARGED 2026-10-30**: `test_ortho.py`, eleven tests on four synthetic photographs of a known plane and a linear
   RPC file (§4).
3. **Every terrain, ortho mapping and stereo name is built or refused with its own reason**, none with the generic
   message, none as a batch. **DISCHARGED 2026-10-30**: `tools/grapy_refusals.py --check` reads none unexamined, and
   the refused names' reasons are tested in both files.
4. **A terrain built in Pro and in grapy from the same points is compared**: the window size levels keep the same
   points where §5.9 (d)'s origin holds, and the z-tolerance levels' heights agree within the tolerance.
   **PARTLY DISCHARGED 2026-10-31**: Pro 3.7 and grapy on 2,500 seeded points in UTM 35N, 100 m square, average spacing
   2 (`tools/pro-compare/adr234-terrain-*.py`). **Window size**: from the window corner Pro uses (found by search: 2, 6
   and 14 modulo 4, 8 and 16 in x; 2, 2 and 10 in y), grapy's rule keeps Pro's points at levels 4, 8 and 16 for ZMIN,
   ZMAX, ZMEAN and ZMINMAX — 642/642, 182/182 and 49/49 for ZMAX, 1,233 for ZMINMAX at 4 — except one point at levels 4
   and 8 of ZMIN and ZMINMAX and level 4 of ZMEAN, an exact tie in height that Pro breaks the other way; a run at
   levels 4, 6 and 10 matched within 4 points. **Changed to Pro's (b)**: each level chooses from full resolution and
   keeps the coarser levels' points (thinning from the level before differed from Pro's 770 points at level 4 by 130 for
   ZMIN, and from its 918 by 276 for ZMEAN); ZMEAN ties within 1e-9; the 2× minimum window. **Not met**: Pro's window corner is not the
   extent's corner; it follows the XY domain (moving the domain's origin 1 m moved the windows 1 m) and the point
   spacing, by a rule its outputs do not pin down, so the same terrain keeps different points in grapy (208 of 642
   shared at level 4) — [D-339](../architecture-debt.md). **DISCHARGED 2026-10-31**, the window corner: Pro 3.7 on 45 terrains of the same 2,500 points (`tools/pro-compare/adr234-terrain-corner-*.py`), the XY domain's origin moved from 0 to 26.8 km and to Pro's default, the point spacing 1, 1.5, 2, 3, 4 and 8: the data is cut into square tiles of `round(spacing × √200000)` m (447, 671, 894, 1,342, 1,789, 3,578) from the domain's origin, and each tile's windows start at its own corner; on an axis whose tile index reaches 23,170 (23,169 measured holding, 23,170 not, at spacings 1 and 2) the tiles are centred on the data's extent instead. The rule reproduces 23,225 of Pro's 23,273 kept points over every run and level (99.8 %); the rest are ties in height and the seam between two tiles, where Pro kept 5 points fewer than the rule at level 8 of one run. grapy now starts its windows by that rule (`_terrain.window_anchors`) from Pro's default XY domain where it is known — the UTM zones, read with arcpy's `SpatialReference(code).domain` — and on the first run's points, unaided, keeps Pro's points at every level of every method but the same height ties as before (ZMAX 642/642, 182/182, 49/49; ZMIN 641 of 642). What stays is recorded in D-339: other coordinate systems' default domains, which grapy does not tabulate, and the seam. **Z-tolerance (c)**: Pro keeps 1,641 points at 0.5 and 193 at
   2, grapy 1,735 and 206; every full-resolution point lies within the tolerance of both (Pro max 0.498 and 1.961,
   grapy 0.499 and 1.981); between the points the two surfaces differ by a mean of 0.095 and 0.551 on a 0.5 m grid.
   The comparison found a grapy bug, fixed: Qhull on UTM coordinates dropped 21 of the 2,500 points as coplanar, so a
   z-tolerance level missed its own bound by 0.8 m and a TIN lost points silently — grapy now triangulates from the
   points' corner, in `_tin`, `_terrain`, `_mptools`, `edit`'s rubber-sheeting and `stats`.
5. **A frame block oriented in Pro is compared with grapy's reading of the same frames table**: the footprints and a
   projected ground point settle §5.9 (a)'s AngleDirection, Polarity, affine and distortion conventions.
   **DISCHARGED 2026-10-31**: Pro 3.7 and grapy on twelve frames of one 300 × 200 camera, 10 µm, f = 50 mm, 1,000 m
   over AverageZ 10, Omega 5, Phi −3, Kappa 30, one convention varied per frame (`tools/pro-compare/adr234-frames-*.py`).
   Each image's two bands are its own column and row, so Pro's orthorectified item says which image position Pro
   projected every ground cell to: about 58,000 cells per frame. **Agreement within 0.002 pixel** for the default film
   map, AngleDirection +1, the principal point, radial distortion and nadir; footprints within 0.0005 m (Pro draws each
   edge with 8 or 6 segments, grapy with one; they coincide where the edge is straight). **Changed to Pro's (b)**: the
   affine maps a pixel's centre (grapy was 0.50 pixel off), the y term's 2·P1·x·y takes x already distorted (0.21
   pixel off; now 0.0013), CenterX and CenterY the footprint's centroid (grapy wrote the perspective centre, 100 m off
   at this tilt), LowPS and HighPS the footprint's width over the columns (0.239 where grapy wrote 0.198). **Not
   settled, recorded**: Pro builds no item with Polarity +1, looking down or up ("Invalid cell size"), so the mirror is
   unmeasured; with distortion Pro's footprint is up to 0.55 m from the distorted corners, which its own
   orthorectification does not follow, and grapy keeps the corners (c).
6. **The owner confirms or changes §5.9** ([Q-202](../open-questions.md)). **DISCHARGED 2026-10-31** — confirmed as built by owner decision in Q-202 (*"Hepsini onayla"*).
