# arcpy.ddd against ArcGIS Pro 3.7

Measured 2026-11-08 (continued 2026-11-12 after the owner's pause), case module `cases/ddd.py`, 129 names (every
built `arcpy.ddd` name; `_defined()` with and without imports finds the same 129, the TIN, LAS, terrain and
multipatch tools being bound by assignment from grapy's underscore modules). Pro writes to a file geodatabase and
grapy to a GeoPackage; TINs and LAS datasets are on disk on both sides, LAS files written byte by byte by the case
module. Multipatches are compared as their distinct vertices (`harness._patch_vertices`: Pro cannot write a
multipatch as WKT, and stores faces as triangle lists where grapy keeps rings), and every shape is now compared with
z (`compare._z_distance`: z interpolated along lines and rings, nearest vertices among points; a shape with z
against one without is a difference).

Many ddd raster tools are arcpy.sa's computation saved (`ddd.Slope` calls `sa.Slope` …). Where the difference lives
in that shared code it is recorded here and left to the sa sweep, which owns `sa.py` and `_surfaces3d`'s sa tools;
several of them (Aspect, Lookup) already match in the sa sweep's branch, so they should match here after the merge.

First run: MATCH 32, DIFF 37, GRAPY-ERROR 22, PRO-ERROR 12, BOTH-ERROR 11, SCHEMA 10, REFUSED 5. Most
GRAPY-ERRORs were one defect: a value-table row lost a Windows path's backslashes (shlex's POSIX mode), so no TIN
could be built; most PRO-ERRORs were the case (terrain sources are named, not pathed; Pro's parameter ranges) or
the harness (multipatch WKT).
Now: MATCH 73, DIFF 36, SCHEMA 11, PRO-ERROR 5, REFUSED 2, BOTH-ERROR 1, GRAPY-ERROR 1 — each recorded below. Of the
non-matching, 19 are shared arcpy.sa code and 6 are not comparable here.

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| ASCII3DToFeatureClass | MATCH | MATCH | — |  |
| AddFeatureClassToTerrain | DIFF | DIFF | recorded | TerrainToPoints at full resolution: Pro includes the hard lines' vertices (33 points), grapy only the mass points (25) |
| AddSurfaceInformation | REFUSED | REFUSED | recorded | shared arcpy.sa code (the sa sweep's): line properties on a raster refused by name in sa.AddSurfaceInformation |
| AddTerrainPyramidLevel | MATCH | MATCH | — |  |
| AddZInformation | REFUSED | MATCH | fixed | field names Z_Min, Z_Max, Z_Mean, Length3D, Min_Slope, Max_Slope, Avg_Slope, Vertex_Cnt; first run REFUSED because CopyFeatures had dropped z |
| AppendTerrainPoints | PRO-ERROR | MATCH | case | Pro names a data source by the feature class's name, not its path (050027) |
| Aspect | DIFF | DIFF | recorded | shared arcpy.sa code (the sa sweep's): outer ring NoData in Pro; the sa sweep matched sa.Aspect in its branch |
| Buffer3D | DIFF | DIFF | recorded | sphere tessellation differs (Pro's vertex rings at other angles); radius agrees |
| BuildTerrain | MATCH | MATCH | — |  |
| ChangeLasClassCodes | MATCH | MATCH | — |  |
| ChangeTerrainReferenceScale | MATCH | MATCH | — |  |
| ChangeTerrainResolutionBounds | BOTH-ERROR | DIFF | recorded | as AddFeatureClassToTerrain (a breakline's vertices at level 120: Pro 26, grapy 25) |
| ClassifyLasBuilding | MATCH | MATCH | — |  |
| ClassifyLasByHeight | MATCH | MATCH | — |  |
| ClassifyLasGround | DIFF | DIFF | recorded | EQUIVALENT (progressive morphological filter): on the 64-point test cloud Pro classes no ground at all, grapy 42 points; a degenerate input for Pro |
| ClassifyLasOverlap | MATCH | MATCH | — |  |
| ClassifyPointCloudUsingTrainedModel | PRO-ERROR | PRO-ERROR | not comparable | needs a model Pro cannot train here (002667) |
| ColorizeLas | DIFF | MATCH | fixed | with no suffix the file is <name>_colorized.las |
| ConstructSightLines | SCHEMA | MATCH | fixed | no DIST_ALONG field for point targets |
| Contour | SCHEMA | SCHEMA | recorded | shared arcpy.sa code (the sa sweep's): Pro writes an Id field (ddd.Contour calls sa.Contour) |
| ContourList | SCHEMA | SCHEMA | recorded | shared arcpy.sa code (the sa sweep's): Pro writes an Id field |
| ContourWithBarriers | DIFF | DIFF | recorded | shared arcpy.sa code (the sa sweep's): _surfaces3d.ContourWithBarriers is sa's too; Pro 9 lines, grapy 8 |
| CopyTin | GRAPY-ERROR | SCHEMA | recorded | Pro's TinNode writes Node_Index, its internal node number (super-nodes first); not derivable |
| CreateTerrain | DIFF | MATCH | case | Pro's Describe answers none of pyramidType, windowSizeMethod, averagePointSpacing, isBuilt; only dataType compared |
| CreateTin | GRAPY-ERROR | SCHEMA | fixed in part | first run GRAPY-ERROR: value-table rows lost a Windows path's backslashes (_parameter._tokens, shared); open: Pro's TinNode writes Node_Index |
| Curvature | MATCH | MATCH | — |  |
| CutFill | PRO-ERROR | MATCH | case | Pro fails (010167 in ZonalStatisticsAsTable) without a scratch workspace; the case sets one |
| DeleteTerrainPoints | PRO-ERROR | MATCH | case | as AppendTerrainPoints |
| DelineateTinDataArea | GRAPY-ERROR | MATCH | fixed | first run GRAPY-ERROR: the TIN's in_features path lost its backslashes (value-table tokenizer, fixed) |
| DetectObjectsFromPointCloudUsingTrainedModel | BOTH-ERROR | BOTH-ERROR | not comparable | needs a model Pro cannot train here (002667) |
| Difference3D | MATCH | MATCH | — |  |
| Divide | DIFF | MATCH | fixed | integer division rounds down (−1/2 is −1), and a whole-valued float constant (2.0) counts as an integer |
| EditTin | BOTH-ERROR | DIFF | fixed in part | height field 'Shape' (Pro's name for the shapes' z) accepted; open: where two hard lines cross, Pro's node z is 119.79 (lines give 125 and 109.8, the surface 123.56) — rule not derived from one crossing |
| EvaluatePointCloudClassificationModel | PRO-ERROR | PRO-ERROR | not comparable | needs a model Pro cannot train here (002667) |
| ExtractLas | MATCH | MATCH | — |  |
| ExtractObjectsFromPointCloud | PRO-ERROR | MATCH | case | Pro needs min_points ≥ 10 |
| ExtrudeBetween | BOTH-ERROR | MATCH | fixed / harness | first run: Pro's multipatch could not be dumped as WKT (harness now dumps its vertices) and grapy hit the backslash defect |
| FeatureClassZToASCII | DIFF | MATCH | case | Pro also writes z.txt.xml (metadata); only the text file is compared |
| FeatureTo3DByAttribute | MATCH | MATCH | — |  |
| FenceDiagram | SCHEMA | SCHEMA | recorded | both write no features for 2D lines on a DEM; Pro's class also has ORIG_OID, Top and Bottom (text) — content unknown, not copied |
| Float | MATCH | MATCH | — |  |
| GeneratePointsAlong3DLines | MATCH | MATCH | — |  |
| HillShade | DIFF | DIFF | recorded | shared arcpy.sa code (the sa sweep's): outer ring NoData and cells off by 1; sa.Hillshade differs too |
| Idw | MATCH | MATCH | — |  |
| Import3DFiles | DIFF | MATCH | fixed | Name keeps the file's extension (b.obj) |
| Inside3D | MATCH | MATCH | — |  |
| Int | MATCH | MATCH | — |  |
| InterpolatePolyToPatch | BOTH-ERROR | MATCH | fixed / harness | first run: Pro's multipatch could not be dumped as WKT (harness now dumps its vertices) and grapy hit the backslash defect |
| InterpolateShape | DIFF | MATCH | fixed | pieces shorter than the sample distance (61, not 60, for 600 m at 10 m); between the outer cell centres and the raster's edge the edge cells' values (Pro kept lines grapy dropped) |
| Intersect3D | DIFF | MATCH | fixed | SourceID_1 and SourceID_2, the inputs' ObjectIDs, before each input's fields |
| Intersect3DLineWithMultiPatch | MATCH | MATCH | — |  |
| IsClosed3D | GRAPY-ERROR | MATCH | fixed | IsClosed is text 'Yes'/'No'; first run GRAPY-ERROR: CopyFeatures of a multipatch class made a polygon class |
| Kriging | DIFF | DIFF | recorded | shared arcpy.sa code (the sa sweep's): EQUIVALENT; every cell differs (max 49), as sa.Kriging |
| LASToMultipoint | SCHEMA | MATCH | fixed | PointCount field |
| LandXMLToTin | DIFF | SCHEMA | fixed in part | one TIN is named the base name alone (was base+surface name); Node_Index as TinNode |
| LasBuildingMultipatch | DIFF | DIFF | recorded | EQUIVALENT roof modelling; vertex sets differ |
| LasDatasetToTin | SCHEMA | SCHEMA | recorded | as TinNode: Node_Index |
| LasPointStatsByArea | SCHEMA | MATCH | fixed | field names Z_Min, Z_Max, Z_Mean, PointCount |
| LocateLasPointsByProximity | MATCH | MATCH | — |  |
| LocateOutliers | DIFF | DIFF | fixed in part | Z field added; open: Pro gives REASON 1 to points only the hard limit flags, where its page says 0 is hard limit and 1 both — Pro against its documentation, not copied |
| Lookup | GRAPY-ERROR | GRAPY-ERROR | recorded | shared arcpy.sa code (the sa sweep's): sa.Lookup needs a raster attribute table grapy does not build on the fly; the sa sweep matched sa.Lookup in its branch |
| MinimumBoundingVolume | PRO-ERROR | DIFF | recorded | CONVEX_HULL of 3D points: Pro's hull keeps vertices grapy drops and the reverse (coplanar points); group NONE is refused by Pro for points (case uses ALL) |
| Minus | MATCH | MATCH | — |  |
| MultiPatchFootprint | DIFF | MATCH | fixed | Name came from Import3DFiles |
| NaturalNeighbor | DIFF | DIFF | recorded | shared arcpy.sa code (the sa sweep's): output grid one cell larger each way than Pro's |
| Near3D | REFUSED | MATCH | fixed | first run REFUSED: CopyFeatures dropped z (fixed in management.CopyFeatures) |
| ObserverPoints | DIFF | DIFF | recorded | shared arcpy.sa code (the sa sweep's): 46 cells differ, as sa.ObserverPoints |
| Plus | MATCH | MATCH | — |  |
| PointFileInformation | MATCH | MATCH | — |  |
| PolygonVolume | GRAPY-ERROR | MATCH | fixed | first run GRAPY-ERROR: the TIN's in_features path lost its backslashes (value-table tokenizer, fixed) |
| PreparePointCloudObjectDetectionTrainingData | BOTH-ERROR | DIFF | recorded | as PreparePointCloudTrainingData (Pro needs multipatch training features) |
| PreparePointCloudTrainingData | DIFF | DIFF | recorded | Pro writes folders train/ and val/, grapy training/, validation/ and side files; a training-data format only each side's trainer reads |
| ReclassByASCIIFile | GRAPY-ERROR | MATCH | case | MATCH with one format throughout; Pro also accepts single values and ranges mixed, which sa's parser (shared) refuses |
| ReclassByTable | MATCH | MATCH | — |  |
| Reclassify | MATCH | MATCH | — |  |
| RegularizeAdjacentBuildingFootprint | DIFF | DIFF | recorded | as RegularizeBuildingFootprint |
| RegularizeBuildingFootprint | DIFF | DIFF | recorded | EQUIVALENT: Pro regularizes the triangle and squares (STATUS 0), grapy returns the triangle unchanged (STATUS 1) and fits the squares differently |
| RemoveFeatureClassFromTerrain | PRO-ERROR | PRO-ERROR | not comparable | Pro fails with 050027 'Invalid Terrain data source name' given the path, the name, a built or an unbuilt terrain |
| RemoveTerrainPoints | BOTH-ERROR | MATCH | case | Pro embeds only a multipoint class that takes part in the overview, in its own group; then both remove the same points |
| RemoveTerrainPyramidLevel | MATCH | MATCH | — |  |
| ReplaceTerrainPoints | PRO-ERROR | MATCH | case | as AppendTerrainPoints; with polygon_features_or_extent Pro failed 999999 |
| SetLasClassCodesUsingFeatures | MATCH | MATCH | — |  |
| SetLasClassCodesUsingRaster | PRO-ERROR | MATCH | case | in_raster is the raster alone |
| Simplify3DLine | SCHEMA | MATCH | fixed | ORIG_OID, the input's ObjectID |
| SkylineBarrier | PRO-ERROR | MATCH | fixed | ORIGFTR_ID −1 when the line has none; SKYLINE_ID, the line's ObjectID (first run PRO-ERROR was the harness: Pro cannot write a multipatch as WKT) |
| Slice | MATCH | MATCH | — |  |
| Slope | DIFF | DIFF | recorded | shared arcpy.sa code (the sa sweep's): Pro leaves the outer ring of cells NoData (236 cells); sa.Slope differs the same way |
| Spline | REFUSED | REFUSED | recorded | shared arcpy.sa code (the sa sweep's): number_points 12 refused by name in sa.Spline |
| SplineWithBarriers | DIFF | DIFF | recorded | shared arcpy.sa code (the sa sweep's): Pro's grid is the barrier+point extent widened (31 × 31), grapy the points' |
| StackProfile | DIFF | MATCH | fixed | raster samples are InterpolateShape's (62 rows for 600 m, not 60); SRC_NAME the path with non-alphanumerics as _, LINE_TARG <line>_<target number>; compared to the centimetre |
| SunShadowFrequency | MATCH | MATCH | — |  |
| SunShadowVolume | DIFF | DIFF | recorded | sun position differs by 4e-4° azimuth, 8e-3° altitude (solar algorithm); the volume's far vertices differ in z |
| SurfaceAspect | GRAPY-ERROR | MATCH | fixed | first run GRAPY-ERROR: the TIN's in_features path lost its backslashes (value-table tokenizer, fixed) |
| SurfaceContour | GRAPY-ERROR | MATCH | fixed | first run GRAPY-ERROR: the TIN's in_features path lost its backslashes (value-table tokenizer, fixed) |
| SurfaceDifference | GRAPY-ERROR | DIFF | recorded | Pro merges same-code regions (5 polygons), grapy keeps slivers of zero area (8); Code sign and areas otherwise agree |
| SurfaceLength | SCHEMA | DIFF | fixed in part | default field SLength (was SURFACE_LENGTH); open: Pro measures a line that reaches past the outer cell centres over its on-data part (598.76 for a 600 m line), grapy leaves it null; values also differ by ~4e-5 relative (sampling rule not derived) |
| SurfaceParameters | DIFF | DIFF | recorded | shared arcpy.sa code (the sa sweep's): EQUIVALENT; cells differ up to 0.10° |
| SurfaceSlope | GRAPY-ERROR | MATCH | fixed | first run GRAPY-ERROR: the TIN's in_features path lost its backslashes (value-table tokenizer, fixed) |
| SurfaceSpot | MATCH | MATCH | — |  |
| SurfaceVolume | GRAPY-ERROR | MATCH | fixed | file layout: ', ' separators, CRLF, ABOVE/BELOW in capitals, z-factor %.6f, areas and volume to 14 significant digits, a path over 32 characters cut to '..' + last 30 (one measurement) |
| TINToMultipatch | BOTH-ERROR | SCHEMA | recorded | Pro writes PART_TYPE (1); meaning not documented, not copied |
| TerrainToPoints | DIFF | DIFF | fixed in part | MULTIPOINT output has PointCount; open: Pro splits a level's 25 points into 4 multipoints (rule not derived), grapy one per source |
| TerrainToRaster | DIFF | DIFF | recorded | the same grid rule as TinRaster |
| TerrainToTin | SCHEMA | SCHEMA | recorded | as TinNode: Node_Index |
| ThinLas | DIFF | MATCH | fixed | with no suffix the file is <name>_thinned.las |
| TileLas | DIFF | MATCH | fixed | XY_COORDS names use the corner's whole part (t_500102_4500102.las) |
| Times | MATCH | MATCH | — |  |
| TinAspect | GRAPY-ERROR | MATCH | fixed | first run GRAPY-ERROR: the TIN's in_features path lost its backslashes (value-table tokenizer, fixed) |
| TinContour | GRAPY-ERROR | MATCH | fixed | first run GRAPY-ERROR: the TIN's in_features path lost its backslashes (value-table tokenizer, fixed) |
| TinDifference | GRAPY-ERROR | DIFF | recorded | Pro writes Area, Volume, Code and 9 polygons; grapy Volume, SArea, Code and 8 — field set and region splitting differ (≈1 day) |
| TinDomain | GRAPY-ERROR | MATCH | fixed | first run GRAPY-ERROR: the TIN's in_features path lost its backslashes (value-table tokenizer, fixed) |
| TinEdge | GRAPY-ERROR | SCHEMA | recorded | Index, Pro's internal edge numbering; not derivable |
| TinLine | BOTH-ERROR | DIFF | recorded | the same crossing z as EditTin |
| TinNode | GRAPY-ERROR | SCHEMA | recorded | Node_Index, Pro's internal node numbering (the 25 points numbered 5–29 in no input order) |
| TinPolygonTag | BOTH-ERROR | DIFF | recorded | Hard_Value_Fill polygons that cross the TIN's hull: Pro clips them to the hull and tags inside (7 regions), grapy tags only the polygon wholly inside (2) — its boundaries extend the hull; a fix touches _tin's polygon handling (≈1 day) |
| TinPolygonVolume | GRAPY-ERROR | MATCH | fixed | first run GRAPY-ERROR: the TIN's in_features path lost its backslashes (value-table tokenizer, fixed) |
| TinRaster | GRAPY-ERROR | DIFF | recorded | Pro lays the grid from the TIN's lower-left corner with round(span / cell) cells; grapy centres the lower-left cell on the corner, as grapy reads the page — Pro against its documentation, not copied |
| TinSlope | GRAPY-ERROR | MATCH | fixed | first run GRAPY-ERROR: the TIN's in_features path lost its backslashes (value-table tokenizer, fixed) |
| TinTriangle | GRAPY-ERROR | DIFF | recorded | Pro's Hillshade fits no azimuth/altitude of the standard model (194 where 255·(cos z cos s + sin z sin s cos(az − asp)) gives 133–184); Tri_Index internal; slope differs at 6e-6 relative (Pro's float z) |
| TopoToRaster | DIFF | DIFF | recorded | shared arcpy.sa code (the sa sweep's): EQUIVALENT; every cell differs (max 34) |
| TopoToRasterByFile | DIFF | DIFF | recorded | shared arcpy.sa code (the sa sweep's): as TopoToRaster (the parameter file round-trips on both sides) |
| TrainPointCloudClassificationModel | PRO-ERROR | PRO-ERROR | not comparable | Pro has no deep learning libraries here (002667) |
| TrainPointCloudObjectDetectionModel | BOTH-ERROR | PRO-ERROR | not comparable | Pro has no deep learning libraries here (002667) |
| Trend | MATCH | MATCH | — |  |
| Union3D | MATCH | MATCH | — |  |
| UpdateFeatureZ | REFUSED | MATCH | fixed | first run REFUSED (CopyFeatures dropped z); the raster surface now reaches its edge as InterpolateShape's |
| Viewshed | DIFF | DIFF | recorded | shared arcpy.sa code (the sa sweep's): 46 cells differ by 1, as sa.Viewshed |
| Viewshed2 | DIFF | DIFF | recorded | shared arcpy.sa code (the sa sweep's): 75 cells' NoData differs, as sa.Viewshed2 |
| Visibility | DIFF | DIFF | recorded | shared arcpy.sa code (the sa sweep's): 46 cells differ by 1, as sa.Visibility |

## Defects fixed beyond arcpy.ddd

- `_parameter._tokens` (and the value-table parsers in `_utility`, `_video`, `locref`, `management.CalculateFields`
  that called `shlex.split` themselves) dropped every backslash of a Windows path in a value-table row. Pro reads the
  path as given. Pinned in `test_pro_sweep_ddd.ValueTables`.
- `management.CopyFeatures` dropped z and m, and made a multipatch class a polygon class. **What else carries this:**
  most of grapy's `_gpkg.create` callers (about 250, analysis and management alike) create their output without the
  input's z/m flags, so z is lost through any general tool on a 3D class; only CopyFeatures is fixed here (open 1).

## Open differences worth a debt row

1. **z through general tools** — grapy's analysis and management tools write 2D outputs from 3D inputs (only
   CopyFeatures keeps z now); every earlier sweep used 2D fixtures, so none saw it. A day to thread `has_z`/`has_m`
   through `_gpkg.create`'s callers, with a 3D fixture in the sweep to measure them.
2. **arcpy.sa code under ddd names** (19 names): edge-ring NoData of Slope/Aspect/HillShade, Contour's Id,
   ContourWithBarriers, the interpolators (Kriging, Spline, NaturalNeighbor, SplineWithBarriers, TopoToRaster),
   visibility (Viewshed, Visibility, ObserverPoints, Viewshed2), SurfaceParameters, AddSurfaceInformation on a raster,
   Lookup without an attribute table. Also `sa._sample` takes the nearest cell beyond the outer cell centres where
   Pro's surface tools carry the edge cells' bilinear values to the raster's edge (`ddd._surface_z` does it for
   InterpolateShape, UpdateFeatureZ and StackProfile), and `sa.Divide` should be checked for ddd.Divide's floor
   division and whole-float constants. The sa sweep owns these.
3. **TIN internals Pro numbers** — TinNode's Node_Index (also LasDatasetToTin, TerrainToTin, CopyTin, CreateTin,
   LandXMLToTin) and TinEdge's Index are Pro's internal numbering; TINToMultipatch's PART_TYPE and TinTriangle's
   Tri_Index likewise. Not derivable; recorded.
4. **TIN rules not derived from one example** — the z where two hard lines cross (EditTin, TinLine); TinTriangle's
   hillshade model; how TerrainToPoints splits a level into multipoints; SurfaceLength over a line that reaches past
   the outer cell centres.
5. **Hard value fill polygons crossing the hull** (TinPolygonTag): Pro clips them to the TIN's data area and tags
   inside; grapy's polygon boundaries extend the hull and only a polygon wholly inside is tagged. About a day in
   `_tin`'s polygon handling.
6. **TinDifference / SurfaceDifference** — Pro's TinDifference writes Area, Volume, Code; grapy Volume, SArea, Code;
   SurfaceDifference merges same-code regions where grapy keeps zero-area slivers. About a day.
7. **Terrain points from breaklines** (AddFeatureClassToTerrain, ChangeTerrainResolutionBounds): Pro's
   TerrainToPoints includes hard lines' vertices; grapy's only mass points. Half a day once the level rule for line
   vertices is measured.
8. **Pro against its own documentation, not copied** — TinRaster/TerrainToRaster lay the grid from the TIN's
   lower-left corner (grapy centres the first cell on it, as grapy reads the page); LocateOutliers gives REASON 1 for
   hard-limit-only points (the page: 0).
9. **EQUIVALENT algorithms that differ from Pro's** — RegularizeBuildingFootprint and its Adjacent twin, Buffer3D's
   sphere tessellation, MinimumBoundingVolume's 3D hull on coplanar points, LasBuildingMultipatch, ClassifyLasGround
   (Pro found no ground on the 64-point test cloud), SunShadowVolume's sun position (4e-4° azimuth).
10. **Point cloud training data** — PreparePointCloudTrainingData and its object-detection twin write a different
    folder layout (Pro train/ and val/); only each side's own trainer reads it, and Pro's trainer cannot run here.

## Not comparable here

- TrainPointCloudClassificationModel, TrainPointCloudObjectDetectionModel, ClassifyPointCloudUsingTrainedModel,
  EvaluatePointCloudClassificationModel, DetectObjectsFromPointCloudUsingTrainedModel: Pro has no deep learning
  libraries on this machine (ERROR 002667).
- RemoveFeatureClassFromTerrain: Pro fails with 050027 "Invalid Terrain data source name" for the feature class's
  path and its name, on a built and an unbuilt terrain.
