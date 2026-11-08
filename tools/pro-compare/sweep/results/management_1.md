# arcpy.management, part 1 (AddAttachments … EnableAttributeRules) against ArcGIS Pro 3.7

Swept 2026-11-08: 126 names, `cases/management_1.py`. *First* is the first run with the comparator's
answer check (below); *now* is after the fixes. `-` in the action column: nothing to do.

| name | first | now | action | note |
|---|---|---|---|---|
| AddAttachments | PRO-ERROR | DIFF | recorded | Pro keys an attachment table on REL_OBJECTID when the class has no GlobalID; grapy adds GlobalIDs and keys on REL_GLOBALID (ADR-228 INFERRED). Same files attached; open difference. |
| AddAttributeRule | DIFF | MATCH | fixed | errorNumber and severity never given answer -1 (were '' and None) - _rules.py. |
| AddCodedValueToDomain | MATCH | MATCH | - |  |
| AddContingentValue | BOTH-ERROR | MATCH | fixed | case read Pro's ContingentFieldValue wrongly (it has code/name/range/type); ContingentValue.subtype is 0 without subtypes (was None). |
| AddFeatureClassToTopology | MATCH | MATCH | - |  |
| AddField | DIFF | MATCH | fixed | a text field's length and the alias were dropped; now kept (TEXT(n) in the GeoPackage, schema-extension title). |
| AddFieldConflictFilter | BOTH-ERROR | BOTH-ERROR | not comparable | needs a branch-versioned class: Pro answers ERROR 000131 on file and mobile geodatabases (enterprise only). |
| AddFields | DIFF | MATCH | fixed | alias, length, default and domain of each row were dropped; now applied. |
| AddFilesToLasDataset | DIFF | MATCH | fixed | Describe pointCount is the LAS headers' total without statistics (was 0, INFERRED). |
| AddFullTextIndex | GRAPY-ERROR | MATCH | fixed | compared on a mobile geodatabase (Pro refuses a file geodatabase, ERROR 002764); ListIndexes now lists the FTS5 index. |
| AddGeometryAttributes | REFUSED | MATCH | fixed | LINE_BEARING built: planar start-to-end bearing, field BEARING. |
| AddGlobalIDs | MATCH | MATCH | - |  |
| AddIncrementingIDField | PRO-ERROR | PRO-ERROR | not comparable | Pro answers ERROR 000186 (only ArcSDE) on file, mobile, GeoPackage and SQLite: enterprise only. |
| AddIndex | GRAPY-ERROR | MATCH | - | case uses ListIndexes (grapy's Describe has no indexes); Index.fields are Field objects in Pro, strings in grapy (arcpy.Index, not this range). |
| AddJoin | SCHEMA | SCHEMA | recorded | joined table's object ID is written as <table>_OBJECTID by Pro, <table>_fid by grapy: grapy's GeoPackage OID column is named fid. Values match. |
| AddRastersToMosaicDataset | MATCH | MATCH | - |  |
| AddRuleToRelationshipClass | BOTH-ERROR | MATCH | fixed | Pro takes subtypes by name (codes answer ERROR 160320; grapy takes both); relationshipRules are objects with -1 for bounds not given (were dicts). |
| AddRuleToTopology | MATCH | MATCH | - |  |
| AddSpatialIndex | GRAPY-ERROR | MATCH | fixed | Describe.hasSpatialIndex added. |
| AddSubtype | MATCH | MATCH | - |  |
| AddXY | MATCH | MATCH | - |  |
| Adjust3DZ | DIFF | MATCH | aligned | the shift is added before the sign is reversed: -(z + shift) (INFERRED -z + shift before). |
| AlterAttributeRule | DIFF | MATCH | fixed | as AddAttributeRule (-1 errorNumber/severity). |
| AlterCodedValueDescription | MATCH | MATCH | - |  |
| AlterDomain | DIFF | MATCH | fixed | a range domain given no range answers (0, 0) (was None). |
| AlterField | REFUSED | MATCH | fixed | type and text length change built on an empty table (Pro: populated answers ERROR 001658); a renamed field keeps its old name as alias. |
| AlterFieldGroup | BOTH-ERROR | MATCH | fixed | case fix (ContingentFieldValue.range) + subtype 0. |
| AlterFields | PRO-ERROR | MATCH | fixed | via AlterField: same-type entries no longer refused; renamed field keeps old name as alias. |
| AlterSubtype | MATCH | MATCH | - |  |
| AlterVersion | BOTH-ERROR | BOTH-ERROR | not comparable | versions need an enterprise geodatabase: ERROR 000837 on file and mobile. |
| Analyze | PRO-ERROR | PRO-ERROR | not comparable | ERROR 000186 (only ArcSDE) on file and mobile geodatabases. |
| AnalyzeControlPoints | BOTH-ERROR | BOTH-ERROR | not comparable | needs a Frame Camera block with control points (ERROR 160144 on a plain mosaic); no such fixture. |
| AnalyzeDatasets | PRO-ERROR | PRO-ERROR | not comparable | ERROR 000837 on file and mobile geodatabases: enterprise only. |
| Append | MATCH | MATCH | - |  |
| AppendControlPoints | BOTH-ERROR | BOTH-ERROR | not comparable | needs ComputeTiePoints' control point schema (Pro 999999 on plain points); no frame-camera fixture. |
| ApplyBlockAdjustment | GRAPY-ERROR | GRAPY-ERROR | recorded | Pro accepts RESET on a mosaic of plain raster datasets (a no-op); grapy refuses a mosaic without Frame Camera items. ADJUST not comparable without a block fixture. |
| AssignDefaultToField | MATCH | MATCH | - |  |
| AssignDomainToField | MATCH | MATCH | - |  |
| BatchBuildPyramids | MATCH | MATCH | - |  |
| BatchCalculateStatistics | DIFF | MATCH | aligned | stored standard deviation is the sample form (n - 1), Pro's; was population (INFERRED). |
| BatchProject | MATCH | MATCH | - |  |
| BatchUpdateFields | PRO-ERROR | MATCH | - | case fix: Pro's scripts read output fields; with no decimals given Pro rounds a DOUBLE script input (1.5 * 2 answered 4) - case now gives 3 decimals. |
| BearingDistanceToLine | MATCH | MATCH | - | PLANAR only (grapy refuses GEODESIC by name). |
| BuildBoundary | MATCH | MATCH | - |  |
| BuildFootprints | DIFF | DIFF | fixed+recorded | CenterX/CenterY now follow the new footprint's centroid; the RADIOMETRY outline itself differs (Pro interpolates edge vertices, 104 vertices, area 316000 against grapy's cell-edge 310600; 1.7 %). |
| BuildPyramids | MATCH | MATCH | - |  |
| BuildPyramidsandStatistics | DIFF | MATCH | aligned | sample standard deviation. |
| BuildRasterAttributeTable | MATCH | MATCH | - |  |
| BuildStereoModel | BOTH-ERROR | BOTH-ERROR | not comparable | needs stereo imagery (Frame Camera or satellite pairs); Pro ERROR 003047 on plain rasters. |
| CalculateCellSizeRanges | MATCH | MATCH | - |  |
| CalculateDefaultClusterTolerance | MATCH | MATCH | - |  |
| CalculateEndTime | MATCH | MATCH | - |  |
| CalculateField | DIFF | MATCH | fixed | a text field the calculation adds is 512 long (was 255). |
| CalculateFields | MATCH | MATCH | fixed | same 512 rule (shared helper); case uses '' for no where clause (None left Pro's field unfilled). |
| CalculateGeometryAttributes | GRAPY-ERROR | MATCH | fixed | LINE_BEARING built; unknown properties refused by name (was a KeyError). |
| CalculateGeometryFieldsAttributeRule | MATCH | MATCH | - |  |
| CalculateStatistics | DIFF | MATCH | aligned | sample standard deviation (also Raster.standardDeviation, GetRasterProperties STD). |
| CalculateValue | DIFF | MATCH | recorded | Pro answers an expression that starts and ends with a quote as the text between them ("'ab' + 'c'" -> "ab' + 'c"), not its value; not copied. Case now avoids it. |
| ChangeVersion | BOTH-ERROR | BOTH-ERROR | not comparable | TRANSACTIONAL needs enterprise; HISTORICAL on an archived mobile class answers ERROR 000301. |
| CheckGeometry | GRAPY-ERROR | SCHEMA | fixed+recorded | grapy's Describe crashed on a bow tie (extent by union) - now by envelopes; problems in lower case as Pro writes them. TEXT_ID stays (grapy's documented GeoPackage field; Pro's file-geodatabase output has none). |
| ClearWorkspaceCache | DIFF | MATCH | fixed | answers 'true' (was ''). |
| Clip | MATCH | MATCH | - |  |
| Compact | MATCH | MATCH | - |  |
| CompositeBands | REFUSED | MATCH | - | first run was a harness limit (grapy RasterToNumPyArray is single-band); case now dumps each band. |
| ComputeBlockAdjustment | BOTH-ERROR | BOTH-ERROR | not comparable | needs a Frame Camera block (Pro 999999 on plain rasters). |
| ComputeControlPoints | BOTH-ERROR | BOTH-ERROR | not comparable | needs a Frame Camera block (Pro 999999). |
| ComputeTiePoints | BOTH-ERROR | BOTH-ERROR | not comparable | needs overlapping Frame Camera imagery (Pro 999999). |
| ConvertCoordinateNotation | DIFF | MATCH | aligned | strings were INFERRED: now Pro's - 8 decimals, padded degrees, rounding carry; UTM truncated; MGRS/USNG 5 digits rounded; GEOREF 1/1000 minute; UTM_BANDS spaced; DDLatLon/DDMLatLon/DMSLatLon; ORIG_OID added. |
| ConvertTimeField | DIFF | DIFF | recorded | Pro writes 2024-03-05 13:45:10 as ...:10.000001 (a microsecond of float error); not copied. |
| Copy | MATCH | MATCH | - |  |
| CopyFeatures | MATCH | MATCH | - |  |
| CopyRaster | REFUSED | MATCH | fixed | fractional values into an integer type were refused: now floor(v + 0.5) and clamped, as Pro; an output with no NoData cell is written without a NoData value, and Raster.noDataValue answers None for it. |
| CopyRows | MATCH | MATCH | - |  |
| CreateDatabaseSequence | MATCH | MATCH | - |  |
| CreateDatabaseView | SCHEMA | SCHEMA | recorded | grapy takes a view's first integer column as its object ID (ADR-240), so rid is not copied; Pro's view has no OID and lists rid as a field. |
| CreateDomain | DIFF | MATCH | fixed | range (0, 0) for a new range domain. |
| CreateFeatureDataset | GRAPY-ERROR | MATCH | fixed | Describe of a feature dataset answers FeatureDataset and its spatial reference (was a Table with none). |
| CreateFeatureclass | MATCH | MATCH | - |  |
| CreateFieldGroup | BOTH-ERROR | MATCH | fixed | case fix + subtype 0. Pro has no arcpy property listing field groups; compared through ListContingentValues. |
| CreateFileGDB | MATCH | MATCH | - |  |
| CreateFishnet | REFUSED | MATCH | fixed+recorded | POLYLINE built (rows+1 and columns+1 whole lines). Pro takes the opposite corner's deltas unrotated on a rotated grid (corner not on the grid); not copied, case avoids it. |
| CreateFolder | MATCH | MATCH | - |  |
| CreateLasDataset | MATCH | MATCH | - |  |
| CreateMosaicDataset | MATCH | MATCH | - |  |
| CreateOrthoCorrectedRasterDataset | BOTH-ERROR | BOTH-ERROR | not comparable | needs RPCs or a Frame Camera mosaic (Pro ERROR 000425 on the DEM). |
| CreateRandomPoints | REFUSED | MATCH | - | compared without minimum_allowed_distance, which grapy refuses by name (count reduction unspecified); fields and counts per CID match. |
| CreateRandomRaster | DIFF | MATCH | aligned | UNIFORM writes 32-bit floats (F32), as Pro (was F64). |
| CreateReferencedMosaicDataset | DIFF | DIFF | recorded | Pro ignored where_clause "Name = 'dem'" and referenced both items; grapy applies it as documented. Not copied. |
| CreateRelationshipClass | DIFF | MATCH | fixed | class keys are (field, role, '') triples as Pro answers. |
| CreateSQLiteDatabase | GRAPY-ERROR | MATCH | - | case describes the workspace before adding a class (grapy describes a one-table GeoPackage as that table). |
| CreateTable | SCHEMA | MATCH | fixed | a field name a template before took is written _1, _2 (was dropped). |
| CreateTopology | MATCH | MATCH | - |  |
| CreateUnRegisteredFeatureclass | DIFF | DIFF | recorded | in a GeoPackage Pro makes OBJECTID INTEGER PRIMARY KEY AUTOINCREMENT, a Shape column, lists it in gpkg_contents and leaves the template's OID out; grapy's ADR-240 design keeps it out of gpkg_contents with an unmaintained OBJECTID and a geom column. |
| CreateUnRegisteredTable | DIFF | DIFF | recorded | as CreateUnRegisteredFeatureclass (OID and template-OID handling). |
| CreateVersion | PRO-ERROR | PRO-ERROR | not comparable | enterprise only (ERROR 000837 on file and mobile). |
| DefineMosaicDatasetNoData | MATCH | MATCH | - |  |
| DefineProjection | MATCH | MATCH | - |  |
| Delete | MATCH | MATCH | - |  |
| DeleteAttributeRule | MATCH | MATCH | - |  |
| DeleteCodedValueFromDomain | MATCH | MATCH | - |  |
| DeleteDatabaseSequence | MATCH | MATCH | - |  |
| DeleteDomain | MATCH | MATCH | - |  |
| DeleteFeatures | MATCH | MATCH | - |  |
| DeleteField | MATCH | MATCH | - |  |
| DeleteFieldGroup | BOTH-ERROR | MATCH | fixed | case fix + subtype 0. |
| DeleteIdentical | MATCH | MATCH | - |  |
| DeleteMosaicDataset | MATCH | MATCH | - |  |
| DeleteMultiple | MATCH | MATCH | - |  |
| DeleteRasterAttributeTable | DIFF | DIFF | recorded | after the tool Pro's ListFields still lists OID, Value, Count (and Raster.hasRAT stays True); grapy removes the table as documented. Not copied. |
| DeleteRows | MATCH | MATCH | - |  |
| DeleteVersion | PRO-ERROR | PRO-ERROR | not comparable | enterprise only. |
| Dice | DIFF | DIFF | recorded | EQUIVALENT algorithm: Pro cuts a 120-vertex circle at x = 310 / 290 (pieces of 29310 and 33494 m2), grapy at the envelope's middle (4 x 31402). Undocumented. |
| DisableArchiving | DIFF | DIFF | recorded | PRESERVE: Pro keeps the history as a feature class <class>_H (listed); grapy renames its internal history table <class>_h, unlisted and in its own schema. |
| DisableAttachments | MATCH | MATCH | - |  |
| DisableAttributeRules | MATCH | MATCH | - |  |
| DisableCOGO | GRAPY-ERROR | MATCH | fixed | Describe.isCOGOEnabled added; COGO fields in Pro's order (Radius before ArcLength) with ArcLength's alias 'Arc Length'. |
| DisableEditorTracking | MATCH | MATCH | - |  |
| DisableLastEditTime | PRO-ERROR | MATCH | - | compared on a mobile geodatabase (file geodatabase: ERROR 004038). |
| Dissolve | DIFF | MATCH | fixed | UNSPLIT_LINES joins lines only at shared ends, each its own feature, without cutting at crossings (grapy unioned them: 3 rows against Pro's 6). |
| DomainToTable | MATCH | MATCH | - |  |
| DowngradeAttachments | DIFF | DIFF | recorded | as AddAttachments: REL_OBJECTID against REL_GLOBALID. |
| Eliminate | MATCH | MATCH | - |  |
| EliminatePolygonPart | SCHEMA | MATCH | fixed | ORIG_FID written. |
| EnableArchiving | DIFF | MATCH | fixed | compared on a mobile geodatabase (file: ERROR 001542); da.Describe isArchived added. |
| EnableAttachments | DIFF | DIFF | fixed+recorded | as AddAttachments. Exists('<class>__ATTACHREL') answered False in grapy; fixed (relationship classes, topologies and network datasets now exist). |
| EnableAttributeRules | MATCH | MATCH | - |  |

Counts, first run: MATCH 54, DIFF 29, BOTH-ERROR 15, PRO-ERROR 9, GRAPY-ERROR 9, REFUSED 6, SCHEMA 4.
Now: MATCH 96, DIFF 11, BOTH-ERROR 10, PRO-ERROR 5, SCHEMA 3, GRAPY-ERROR 1. Every name that is not MATCH has a
reason above: 15 are not comparable on this machine (enterprise geodatabase, or a Frame Camera / stereo / RPC imagery
block this sweep has no fixture for), the rest are recorded differences.

## Harness changes made with this module

- `compare.py` compared a case's returned answer (`result.json`) only when the case wrote no dataset, so 12
  differences here read as MATCH on the first pass. The coordinator's fix (69bfebb2) is cherry-picked; on top of it,
  nested lists and dicts in an answer are now compared element by element with the numeric tolerance. Without that,
  Adjust3DZ reads DIFF only because Pro stores z to 0.1 mm (-65.6167 against grapy's -65.616667, 5e-7 relative).
  The verdicts above are with both.
- `harness.py` leaves GlobalID fields out of a dump: they are random GUIDs on both sides.

## Open differences worth a debt row

1. **Attachments without GlobalIDs** (AddAttachments, EnableAttachments, DowngradeAttachments): Pro keys the attachment
   table on `REL_OBJECTID` (Integer) and adds no GlobalID; grapy adds GlobalIDs and keys on `REL_GLOBALID` (ADR-228
   INFERRED, now contradicted). Touches `_attachments.py`, AddAttachments/RemoveAttachments/export paths and their
   tests; about half a day.
2. **Unregistered tables in a GeoPackage** (CreateUnRegisteredTable, CreateUnRegisteredFeatureclass): Pro writes
   `OBJECTID INTEGER PRIMARY KEY AUTOINCREMENT`, a `Shape` column, a `gpkg_contents` row, and leaves a template's OID
   out. grapy's ADR-240 design (unlisted, unmaintained OBJECTID, `geom`) differs on each point. Needs a decision, since
   a GeoPackage has no unregistered state in Pro at all; AddIncrementingIDField depends on it.
3. **Archive history kept by DisableArchiving PRESERVE**: Pro keeps a listed feature class `<class>_H` with the class's
   fields; grapy renames its own history table `<class>_h` (gdb_* columns, moments not dates), unlisted. About half a
   day.
4. **Database views** (CreateDatabaseView): grapy takes a view's first integer column as its object ID (ADR-240), so
   CopyRows drops it; Pro's view has no OID and keeps the column as a field.
5. **GeoPackage OID name**: grapy names the OID `fid`, Pro `OBJECTID`; it shows wherever a name is derived from it
   (AddJoin's `<table>_OBJECTID`).
6. **Undocumented algorithms**: Dice's cut positions, BuildFootprints' RADIOMETRY outline (1.7 % of area). EQUIVALENT
   choices; no fix without a specification.
7. **ApplyBlockAdjustment RESET** on a mosaic without Frame Camera items is a no-op in Pro and an error in grapy (small).
8. **Not comparable here**: the eight ortho-mapping tools (AnalyzeControlPoints, AppendControlPoints,
   ApplyBlockAdjustment ADJUST, BuildStereoModel, ComputeBlockAdjustment, ComputeControlPoints, ComputeTiePoints,
   CreateOrthoCorrectedRasterDataset) need a synthetic Frame Camera block (images, frames and cameras tables) as a sweep
   fixture; the versioning and enterprise tools (AddFieldConflictFilter, AlterVersion, ChangeVersion, CreateVersion,
   DeleteVersion, Analyze, AnalyzeDatasets, AddIncrementingIDField) need an enterprise geodatabase.

Pro answers recorded and not copied: CalculateValue strips an expression's outer quotes; ConvertTimeField adds a
microsecond; CreateReferencedMosaicDataset ignored its where clause; DeleteRasterAttributeTable leaves the table
listed; CreateFishnet reads a rotated grid's opposite corner unrotated; BatchUpdateFields rounds a script's DOUBLE input
when no decimals are given.

Seen on the way, outside this range: Pro's `Index.fields` are Field objects, grapy's strings (arcpy.Index); grapy's
Describe has no `indexes`; grapy's RasterToNumPyArray refuses several bands, which the harness's raster dump uses.
