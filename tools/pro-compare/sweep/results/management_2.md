# arcpy.management, second half (EnableCOGO to the last) against ArcGIS Pro 3.7

Measured 2026-11-08, case module `cases/management_2.py`, 126 names (the built `arcpy.management` names from index 126
of `sorted()` on). Pro writes to a file geodatabase and grapy to a GeoPackage; differences that are only the
workspace's (Shape_Length/Shape_Area, a path in MERGE_SRC) are left out in the case, each with a comment. Tools that
change data in place change a copy; folders a case writes into are emptied first (a second run into the same folder
names files differently in Pro: `GenerateID0.csv`, `a(2).las`).

First run: MATCH 45, BOTH-ERROR 27, DIFF 23, REFUSED 11, PRO-ERROR 9, GRAPY-ERROR 8, SCHEMA 3. (The first run is the
first of three batches of cases each; most BOTH-ERRORs and PRO-ERRORs were the case: attribute rules need global IDs
in Pro, an `O(..., "file")` path the harness then tried to dump, `drop` as a table name, EXIF rationals.)
Run again 2026-11-12 after the column names (ADR-289 §5.1): unchanged, MATCH 104, DIFF 9, BOTH-ERROR 7, PRO-ERROR 5,
SCHEMA 1 — no case here compared the names (open 9 is a file geodatabase's OID length).
Now (attachments run again 2026-11-12): MATCH 104, DIFF 9, BOTH-ERROR 7, PRO-ERROR 5, SCHEMA 1 — every one recorded below as not comparable or an open
difference.

Where grapy refuses one option of a tool by name (REFUSED in the first run), the case was narrowed to the options
grapy builds, so the rest is compared, and the refused option is listed in open 8.

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| EnableCOGO | DIFF | MATCH | fixed | fields in Pro's order (Direction, Distance, Radius, ArcLength, Radius2); Describe.isCOGOEnabled added |
| EnableEditorTracking | MATCH | MATCH | — |  |
| EnableLastEditTime | PRO-ERROR | PRO-ERROR | not comparable | Pro refuses a file geodatabase (ERROR 004038 Last Edit Time is not supported) |
| EncodeField | SCHEMA | MATCH | fixed | fields named <METHOD>_<field>_<category> (ONEHOT_cat_a), long integers |
| EvaluateRules | PRO-ERROR | MATCH | case | Pro needs global IDs and editor tracking for a batch rule (ERROR 002710, 003324) |
| Export3DObjects | BOTH-ERROR | DIFF | recorded | Pro writes OID_1/cube.obj with .prj, .wld3 and _ESRI3DO.json; grapy 1/1.obj (open 6) |
| ExportAttachments | BOTH-ERROR | MATCH | fixed | a file left in the folder is overwritten, not renamed _1; the case's name_format was ATTRIBUTE_NAME, which Pro refuses |
| ExportAttributeRules | BOTH-ERROR | MATCH | fixed | BOM, a 21st column SUBTYPES ([]) and CHECKPARAMETERS as an empty property set |
| ExportContingentValues | BOTH-ERROR | MATCH | fixed | true/false, SUBTYPE 0 without subtypes, a range as min#max to six decimals (import reads both forms) |
| ExportFrameAndCameraParameters | BOTH-ERROR | BOTH-ERROR | not comparable | Pro fails (999999) without a frame-camera mosaic dataset from an ortho mapping workspace; no frame imagery here |
| ExportMosaicDatasetGeometry | SCHEMA | SCHEMA | recorded | geometry matches; footprint fields: Pro ItemTS, TypeID, UriHash, grapy Raster, SourceOID (open 7) |
| ExportMosaicDatasetItems | DIFF | MATCH | fixed | extension .TIF in capitals |
| ExportMosaicDatasetPaths | DIFF | MATCH | fixed | no Type field (SourceOID, Path only) |
| ExportRasterWorldFile | MATCH | MATCH | — |  |
| ExportTopologyErrors | GRAPY-ERROR | MATCH | fixed | outputs <base>_point, _line, _poly and field isException (were <base>PointsErrors …, IsException) |
| ExtractSubDataset | PRO-ERROR | PRO-ERROR | not comparable | Pro does not open the case's NetCDF-3 file (000732); an HDF5 writer exists only in Pro's Python here |
| FeatureCompare | MATCH | MATCH | — |  |
| FeatureEnvelopeToPolygon | MATCH | MATCH | — |  |
| FeatureToLine | DIFF | DIFF | recorded | same linework; Pro splits poly 1's outer arc once more, at (250, 50), a vertex nothing touches — rule not derivable from one example (open 1) |
| FeatureToPoint | DIFF | MATCH | fixed | INSIDE is the centroid when it lies inside (a triangle's was a representative point) |
| FeatureToPolygon | DIFF | MATCH | fixed | a line input brings no fields; a polygon input's empty values are 0 and "", not null |
| FeatureVerticesToPoints | DIFF | MATCH | fixed | ALL includes a ring's closing vertex and holes, rings walked clockwise; DANGLE built with DANGLE_LEN (was every vertex) |
| FileCompare | MATCH | MATCH | — |  |
| FindIdentical | REFUSED | MATCH | case | xy tolerance refused by grapy (recorded, open 8); exact shapes compared |
| Flip | MATCH | MATCH | — |  |
| GenerateAttachmentMatchTable | GRAPY-ERROR | MATCH | fixed | fields MatchID and Filename (were MATCHID, FILENAME); da cursors now take a field in any case |
| GenerateBlockAdjustmentReport | BOTH-ERROR | BOTH-ERROR | not comparable | as ExportFrameAndCameraParameters (needs a block adjustment of frame imagery) |
| GenerateContingentValues | BOTH-ERROR | MATCH | fixed | a range-domain field is a RANGE of its rows' values per combination, combinations in value order, True/False, min#max |
| GenerateIDAttributeRule | GRAPY-ERROR | MATCH | fixed | no configuration means one for every row; Pro's file names (GenerateID.csv, ID_Table.csv, create_database_sequences.py, second run …0), rule name, flags |
| GeneratePointsAlongLines | MATCH | MATCH | — |  |
| GenerateSpatialJoinAttributeRule | BOTH-ERROR | MATCH | fixed | file Spatial_Join.csv, rule "Spatial Join" with no description; TRIGGERINGFIELDS empty |
| GenerateSymbolRotationAttributeRule | GRAPY-ERROR | MATCH | fixed | file and rule SymbolRotation; a line class with no orientation field orients by its object ID |
| GenerateTessellation | DIFF | MATCH | fixed | Pro's lattice: a cell centred on the extent's corner, ceil(extent/step)+1 cells per axis; GRID_ID column letter, row number from the top |
| GenerateTransectsAlongLines | DIFF | DIFF | fixed in part | transects run right to left (were left to right). Left: Pro tilts line 2's transects by 0.64° on a straight line (open 2) |
| GenerateUpdateRelatedAttributeRule | BOTH-ERROR | MATCH | fixed | file Update_Related.csv, rule "Update Related", Pro's description, update only |
| GeoTaggedPhotosToPoints | BOTH-ERROR | MATCH | case | EXIF rationals written as numbers |
| GeodeticDensify | REFUSED | MATCH | case | LOXODROME refused by grapy (recorded, open 8); GEODESIC on lines and polygons |
| GetCellValue | MATCH | MATCH | — |  |
| GetCount | MATCH | MATCH | — |  |
| GetRasterProperties | DIFF | MATCH | fixed | STD is the sample one; UNIQUEVALUECOUNT of a float raster refused; sensor properties UNKNOWN, SOURCETYPE Generic |
| Import3DObjects | BOTH-ERROR | DIFF | fixed in part | Geometry.extent carries Z for a z-aware shape. Left: Pro's 3D object class has GlobalID and 15 ESRI3DO_* fields, grapy Name (open 6) |
| ImportAttributeRules | BOTH-ERROR | MATCH | case | global IDs |
| ImportContingentValues | BOTH-ERROR | MATCH | fixed | reads SUBTYPE 0 of a table without subtypes and min#max ranges |
| ImportMosaicDatasetGeometry | MATCH | MATCH | — |  |
| Integrate | DIFF | DIFF | recorded | Pro moves clustered points to a common point (the cluster mean for simple clusters, iterated); grapy snaps to a vertex by ADR-225 §5.3 (open 3) |
| InterpolateFromPointCloud | DIFF | MATCH | fixed | the grid has a cell centred on the points' lower-left corner (Pro 0..600 for points 10..590) |
| JoinField | MATCH | MATCH | — |  |
| LasDatasetStatistics | BOTH-ERROR | DIFF | recorded | Pro's report layout (Item, Category, Pt_Cnt … with returns, class codes by name, attributes and flags) differs from grapy's per-class table (open 5) |
| LasPointStatsAsRaster | DIFF | MATCH | fixed | a point on a row boundary falls in the row above (cells hold their left and bottom edges) |
| MakeFeatureLayer | MATCH | MATCH | — |  |
| MakeLasDatasetLayer | DIFF | MATCH | fixed | as LasPointStatsAsRaster |
| MakeMosaicLayer | MATCH | MATCH | — |  |
| MakeTableView | MATCH | MATCH | — |  |
| MakeTinLayer | DIFF | MATCH | fixed | Describe of a TIN layer answers TinLayer |
| MatchControlPoints | BOTH-ERROR | BOTH-ERROR | not comparable | as ExportFrameAndCameraParameters |
| MatchPhotosToRowsByTime | BOTH-ERROR | MATCH | case | EXIF rationals |
| Merge | PRO-ERROR | MATCH | case | Pro refuses one input twice (000400); MERGE_SRC compared by its last name |
| MigrateDateFieldToHighPrecision | MATCH | MATCH | — |  |
| MigrateObjectIDTo64Bit | DIFF | DIFF | recorded | a file geodatabase's OID length goes 4 to 8; a GeoPackage's is 64-bit already and ListFields answers 4 (open 9) |
| MinimumBoundingGeometry | REFUSED | DIFF | case | MBG_FIELDS of a convex hull refused by grapy (recorded, open 8). CIRCLE 1.25× the 0.5 % tolerance: Pro's true circle densified (not a defect) |
| Mirror | MATCH | MATCH | — |  |
| Mosaic | MATCH | MATCH | — |  |
| MosaicToNewRaster | REFUSED | MATCH | case | a cell size other than the inputs' refused by grapy (recorded, open 8) |
| MultipartToSinglepart | MATCH | MATCH | — |  |
| PivotTable | MATCH | MATCH | — |  |
| PointsToLine | MATCH | MATCH | — |  |
| PolygonToLine | REFUSED | MATCH | case | IDENTIFY_NEIGHBORS over overlapping polygons refused by grapy (recorded, open 8); the four apart compared |
| Project | MATCH | MATCH | — |  |
| ProjectLAS | MATCH | MATCH | — | (a second run into the same folder: Pro writes a(2).las — the case now uses a fresh folder) |
| PruneBranchHistory | BOTH-ERROR | MATCH | fixed | a GeoPackage with no versions: ERROR 161027 (was SQLite's no such table); answer compared as Pro's refusal |
| RasterToDTED | REFUSED | MATCH | fixed | tiles in a dted<level> folder (dted2/e027/n40.dt2); Pro's .aux.xml, .ovr, .xml sidecars not written |
| RebuildIndexes | PRO-ERROR | PRO-ERROR | not comparable | Pro takes an enterprise geodatabase only (000837) |
| RecalculateFeatureClassExtent | DIFF | DIFF | recorded | Pro keeps a stored extent until recalculated; grapy's Describe extent is always live (open 4) |
| ReclassifyField | BOTH-ERROR | MATCH | fixed | class field is <name> (was <name>_CLASS); a double's range to six decimals, the next class 0.000001 above; quantile class sizes n//k (+1 for the first n%k) |
| ReconcileVersion | BOTH-ERROR | BOTH-ERROR | not comparable | enterprise geodatabase (000837) |
| ReconcileVersions | BOTH-ERROR | PRO-ERROR | not comparable | enterprise geodatabase (000837) |
| RegisterAsVersioned | BOTH-ERROR | PRO-ERROR | not comparable | Pro: a file or mobile geodatabase cannot be versioned (000133) |
| RegisterWithGeodatabase | BOTH-ERROR | MATCH | fixed | ERROR 001050 for a class already registered (was 000800); compared as Pro's refusal |
| RemoveAttachments | BOTH-ERROR | MATCH | case | "drop" is an SQL word Pro refuses as a table name |
| RemoveContingentValue | MATCH | MATCH | — |  |
| RemoveDomainFromField | MATCH | MATCH | — |  |
| RemoveFeatureClassFromTopology | GRAPY-ERROR | MATCH | fixed | takes a bare class name of the topology's dataset |
| RemoveFieldConflictFilter | BOTH-ERROR | BOTH-ERROR | not comparable | Pro: the class must be versioned (000131), enterprise only |
| RemoveFilesFromLasDataset | PRO-ERROR | MATCH | case | Pro names the dataset's files by their Windows path |
| RemoveIndex | MATCH | MATCH | — |  |
| RemoveJoin | DIFF | MATCH | case | a file geodatabase's Shape_Length/Shape_Area left out |
| RemoveRastersFromMosaicDataset | MATCH | MATCH | — |  |
| RemoveRuleFromRelationshipClass | GRAPY-ERROR | MATCH | fixed | Describe's relationshipRules are objects (were dicts); a side without subtypes has code 0 |
| RemoveRuleFromTopology | BOTH-ERROR | MATCH | case | the rule is named with its classes' object-class ids, which differ by workspace; the case finds it |
| RemoveSpatialIndex | MATCH | MATCH | — |  |
| RemoveSubtype | MATCH | MATCH | — |  |
| Rename | MATCH | MATCH | — |  |
| ReorderAttributeRule | PRO-ERROR | MATCH | case | global IDs |
| RepairGeometry | MATCH | MATCH | — |  |
| RepairMosaicDatasetPaths | DIFF | MATCH | case | the raster moved (a broken path) and the paths given as Windows writes them |
| Resample | REFUSED | MATCH | case | grapy builds NEAREST only and refuses a centre on a cell edge (recorded, open 8) |
| SelectLayerByAttribute | MATCH | MATCH | — |  |
| SelectLayerByLocation | REFUSED | MATCH | case | CROSSED_BY_THE_OUTLINE_OF refused by grapy (recorded, open 8) |
| SetClusterTolerance | MATCH | MATCH | — |  |
| SetDefaultSubtype | MATCH | MATCH | — |  |
| SetMosaicDatasetProperties | DIFF | MATCH | fixed | allowed compressions list the default first; defaultResamplingMethod answered by name (Bilinear Interpolation) |
| SetSubtypeField | MATCH | MATCH | — |  |
| SetValueForRangeDomain | MATCH | MATCH | — |  |
| Shift | REFUSED | MATCH | case | the snap raster refused by grapy (recorded, open 8) |
| Sort | SCHEMA | MATCH | fixed | ORIG_FID written |
| SortCodedValueDomain | MATCH | MATCH | — |  |
| SplitLine | DIFF | MATCH | fixed | a polygon's ring walked clockwise from its first vertex (ORIG_SEQ as Pro) |
| SplitLineAtPoint | MATCH | MATCH | — |  |
| StandardizeField | MATCH | MATCH | — |  |
| SubsetFeatures | MATCH | MATCH | — | 100 % compared row by row; 40 % and 7 rows by count (random) |
| SynchronizeMosaicDataset | MATCH | MATCH | — |  |
| TableCompare | MATCH | MATCH | — |  |
| TableToDomain | PRO-ERROR | MATCH | case | a SHORT code field for an existing SHORT domain (Pro 000307); domains in name order |
| TableToEllipse | MATCH | MATCH | — |  |
| TableToRelationshipClass | MATCH | MATCH | — |  |
| TransformField | REFUSED | MATCH | fixed | fields <field>_LOGARITHM, _SQUARE_ROOT, _INVERSE, _BOX_COX (values already equal) |
| TransposeFields | DIFF | MATCH | fixed | a whole double written without .0 |
| TrimArchiveHistory | BOTH-ERROR | BOTH-ERROR | not comparable | Pro: datasets served through an RDBMS only (002913) |
| TruncateTable | MATCH | MATCH | — |  |
| UnregisterAsVersioned | BOTH-ERROR | BOTH-ERROR | not comparable | as RegisterAsVersioned |
| UnsplitLine | DIFF | MATCH | fixed | statistics counted only the lines merged (crossing lines were counted in each other) |
| UpgradeAttachments | GRAPY-ERROR | MATCH | fixed | the key is REL_OBJECTID on a class without global IDs, as Pro's (ADR-289 §5.4, run again 2026-11-12; open 10 built) |
| UpgradeGDB | PRO-ERROR | MATCH | fixed | a current geodatabase is refused with ERROR 001046, the check alone too |
| ValidateTopology | GRAPY-ERROR | MATCH | — |  |
| XYTableToPoint | MATCH | MATCH | — |  |
| XYToLine | REFUSED | MATCH | case | GEODESIC refused by grapy (recorded, open 8); PLANAR compared |

## Not comparable on this machine

- **Enterprise geodatabase only in Pro:** RegisterAsVersioned, UnregisterAsVersioned (000133 on a file or mobile
  geodatabase), ReconcileVersion, ReconcileVersions, RebuildIndexes (000837), TrimArchiveHistory (002913),
  RemoveFieldConflictFilter (000131). The cases run on a file geodatabase on both sides so grapy's own versions
  (ADR-227) still run; grapy refuses the same unversioned class with its own messages. Pro 3.7 accepts neither a file
  nor a mobile geodatabase for these (measured; grapy has no mobile geodatabase, `CreateMobileGDB` is refused).
- **EnableLastEditTime:** Pro refuses a file geodatabase (004038, "Last Edit Time is not supported").
- **Ortho mapping:** ExportFrameAndCameraParameters, GenerateBlockAdjustmentReport, MatchControlPoints need a mosaic
  dataset of frame-camera imagery with a block adjustment; Pro fails (999999) on a plain mosaic dataset and there is no
  frame imagery here. grapy refuses the same input ("holds no Frame Camera …").
- **ExtractSubDataset:** Pro does not open the case's NetCDF-3 file (000732); the only HDF5 writer on this machine is in
  Pro's Python, so no file both sides can build was found.

## Open differences worth a debt row

1. **FeatureToLine** — the same linework, but Pro splits poly 1's outer arc once more, at (250, 50), a vertex nothing
   else touches (9 lines against grapy's 10, which splits poly 2's inner arc at its ring start). One example does not
   give the rule; a second fixture is needed before any change.
2. **GenerateTransectsAlongLines** — Pro tilts the transects of the straight vertical line 2 by 0.64° (tan = 1/90);
   the others are square. Looks wrong against the page ("perpendicular"); not copied.
3. **Integrate** — Pro moves the points of a cluster to a common new point (the plain mean for simple clusters, an
   iterated merge for chained ones); grapy snaps onto an existing vertex by ADR-225 §5.3. A new clustering algorithm:
   about two days, and it touches ADR-225's rule shared with the topology tools.
4. **RecalculateFeatureClassExtent** — Pro keeps a stored extent that deletes do not shrink until this tool runs;
   grapy's Describe extent is computed live, so the tool has nothing to do. Storing extents in `gpkg_contents` and
   reading them in Describe: about a day, wide reach (every Describe).
5. **LasDatasetStatistics** — Pro's DATASET report is a different table (Item, Category, Pt_Cnt, Percent, Z_Min,
   Z_Max, Intensity_Min/Max, Synthetic_Pt_Cnt, Range_Min/Max; rows for returns, class codes by name such as
   `2_Ground`, attribute ranges and class flags); grapy writes one row per class code. Half a day, with the return rows
   for multi-return data still to measure.
6. **3D objects** — Import3DObjects: Pro's class has GlobalID and fifteen `ESRI3DO_*` fields, grapy a Name field;
   Export3DObjects: Pro writes `OID_1/cube.obj` with `.prj`, `.wld3` and `cube_ESRI3DO.json`, grapy `1/1.obj`. The
   3D object feature layer schema; a day or more.
7. **ExportMosaicDatasetGeometry** — the footprint fields: Pro writes ItemTS, TypeID, UriHash and not Raster or
   SourceOID. grapy's mosaic catalog schema (ADR-230); geometry matches.
8. **Options grapy refuses by name** (cases narrowed): FindIdentical's xy tolerance, GeodeticDensify LOXODROME,
   MinimumBoundingGeometry MBG_FIELDS of a convex hull, MosaicToNewRaster with another cell size, PolygonToLine
   IDENTIFY_NEIGHBORS over overlapping polygons, SelectLayerByLocation CROSSED_BY_THE_OUTLINE_OF, Shift's snap raster,
   XYToLine GEODESIC, Resample BILINEAR/CUBIC/MAJORITY and a new cell centre on an input cell's edge.
9. **MigrateObjectIDTo64Bit** — a file geodatabase's OID field goes from length 4 to 8; a GeoPackage's object IDs are
   64-bit already and ListFields answers 4 before and after. A workspace difference; whether ListFields should answer 8
   for a GeoPackage OID needs Pro's answer on a GeoPackage.
10. ~~**Attachments keyed by object ID** — Pro's attachment table on a class without global IDs relates by REL_OBJECTID
    (Integer); grapy always by REL_GLOBALID (ADR-228). Found by UpgradeAttachments; every attachment tool carries it.~~
    **Built 2026-11-12 (ADR-289 §5.4)**; ExportAttachments, RemoveAttachments and UpgradeAttachments run again: MATCH.
11. **Contingent values are not enforced by Pro's cursors** — measured with a RESTRICT field group: Pro's InsertCursor and
    UpdateCursor accept a combination no contingent value allows; grapy's raise RuleError (ADR-235 §5.8). An ADR-level
    choice in `grapy._rules`, not changed here; GenerateContingentValues' case uses DO_NOT_RESTRICT to compare.
12. **Shared with the other half:** `_rulegen._file`/`_write` now name a rule's file after the rule when the tool gives
    one; CalculateGeometryFieldsAttributeRule (the other agent's range) still writes `<table>.csv` and was not measured.
