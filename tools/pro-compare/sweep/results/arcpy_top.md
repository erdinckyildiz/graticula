# arcpy (top-level names) against ArcGIS Pro 3.7

Measured 2026-11-08, case module `cases/arcpy_top.py`, 115 names. Answers that depend on the workspace's kind (name
validation, delimiters, Describe, ListFields, ListIndexes) are compared on a GeoPackage on both sides, since that is the
workspace grapy writes; the first run compared Pro's file geodatabase with grapy's GeoPackage and was moved.

First run: MATCH 24, DIFF 56, GRAPY-ERROR 22, REFUSED 4, BOTH-ERROR 5, PRO-ERROR 3, SCHEMA 1 (the BOTH-ERROR and
PRO-ERROR cases were faults in the case: arguments Pro's signature does not take).
Now: MATCH 84, DIFF 30, PRO-ERROR 1.

Note on the harness: compare.py ignores `result.json` when a case also writes an output (`O(...)`), so for FeatureSet,
RecordSet, FieldMap, FieldMappings, AlterAliasName, Row and NumPyArrayToRaster the answers were also compared label by
label by hand; the one difference left there is FeatureSet's (below).

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| AddError | DIFF | DIFF | recorded | message model (open 1) |
| AddFieldDelimiters | DIFF | MATCH | case | Pro on a GeoPackage quotes `"val"` as grapy does; the first run was Pro's file geodatabase |
| AddIDMessage | DIFF | DIFF | recorded | message model (open 1) |
| AddMessage | DIFF | DIFF | recorded | message model (open 1) |
| AddReturnMessage | DIFF | DIFF | recorded | message model (open 1) |
| AddToolbox | GRAPY-ERROR | MATCH | case | the .pyt imports `grapy as arcpy` where there is no arcpy |
| AddWarning | DIFF | DIFF | recorded | message model (open 1) |
| AlterAliasName | GRAPY-ERROR | MATCH | fixed | Describe had no aliasName |
| ArealUnitConversionFactor | MATCH | MATCH | — | |
| Array | BOTH-ERROR | MATCH | fixed | replace, remove(index), next/reset added (case dropped clone, which Pro rejects) |
| AsShape | DIFF | MATCH | fixed | WKT form (see FromWKT) |
| CheckExtension | DIFF | MATCH | fixed | unknown code: "Unrecognized extension code X" |
| CheckInExtension | MATCH | MATCH | — | |
| CheckOutExtension | MATCH | MATCH | — | |
| CheckProduct | DIFF | MATCH | fixed | ArcInfo Available, ArcEditor/ArcView NotLicensed, unknown "" |
| ClearEnvironment | DIFF | DIFF | recorded | workspace default (open 3) |
| Command | DIFF | DIFF | recorded | message model (open 1): GetCount writes no "Row Count" message in grapy |
| CopyParameter | DIFF | MATCH | fixed | outside a tool run: does nothing |
| CreateObject | PRO-ERROR | DIFF | recorded | Pro answers factoryCode 0 for CreateObject("SpatialReference", 32635) and RuntimeError for an unknown name (grapy 32635, ValueError); Pro looks to ignore the option — not copied |
| CreateRandomValueGenerator | MATCH | MATCH | — | |
| CreateScratchName | DIFF | MATCH | fixed | a suffix already ending in the extension no longer doubles it |
| CreateUniqueName | DIFF | MATCH | fixed | in a GeoPackage the names are its tables (`points0`) |
| CrossValidationResult | REFUSED | MATCH | case | ga.IDW refuses the default search neighbourhood (ga's range); with one given, count, meanError and RMS equal Pro's |
| Cursor | BOTH-ERROR | MATCH | fixed | the legacy cursors' generator held the cursor (cycle), so `del cursor` left the GeoPackage locked |
| Describe | DIFF | DIFF | fixed in part | added aliasName, extension, hasSpatialIndex, canVersion, isVersioned, workspaceFactoryProgID, raster extension/compressionType; left: OBJECTID/Shape vs fid/geom (open 4) and a one-table GeoPackage described as its table (open 5) |
| EnvManager | MATCH | MATCH | — | |
| Exists | DIFF | MATCH | fixed | a bare name is looked for in env.workspace |
| Extent | GRAPY-ERROR | MATCH | fixed | documented 9-argument signature (reference ninth), Z/M, corners, relations, projectAs, str "0 0 100 50 NaN NaN NaN NaN" |
| FeatureSet | GRAPY-ERROR | MATCH* | fixed | empty FeatureSet().JSON raised; *answer still differs only by the file geodatabase's Shape_Length/Shape_Area fields (inherent) |
| Field | DIFF | MATCH | fixed | blank Field(): type "" and length 0. Pro's blank scale is unset memory (-1, 6881395, 6357106 in three runs); not copied, left out of the case |
| FieldInfo | MATCH | MATCH | — | |
| FieldMap | MATCH | MATCH | — | |
| FieldMappings | SCHEMA | MATCH | fixed | conversion.ExportFeatures ignored field_mapping (removed map's field was written); now follows it as ExportTable does |
| Filter | DIFF | MATCH | fixed | a new parameter's filter is ValueList with [] |
| FromCoordString | DIFF | MATCH | built | MGRS and USNG (was refused) |
| FromGeohash | MATCH | MATCH | — | |
| FromWKB | GRAPY-ERROR | MATCH | fixed | spatial_reference argument |
| FromWKT | DIFF | MATCH | fixed | WKT: polygons always MULTIPOLYGON, outer rings counterclockwise and holes clockwise; polylines MULTILINESTRING (also SHAPE@WKT) |
| Geometry | GRAPY-ERROR | MATCH | fixed | Geometry(type, inputs, sr); CopyFeatures(..., Geometry()) answers a geometry list |
| GeostatisticalDatasets | REFUSED | MATCH | case | as CrossValidationResult |
| GetAllMessages | DIFF | DIFF | recorded | message model (open 1) |
| GetArgumentCount | DIFF | MATCH | fixed | outside a tool run: the script's arguments |
| GetMaxSeverity | DIFF | DIFF | recorded | message model (open 1) |
| GetMessage | DIFF | DIFF | recorded | message model (open 1) |
| GetMessageCount | DIFF | DIFF | recorded | message model (open 1) |
| GetMessages | DIFF | DIFF | recorded | message model (open 1) |
| GetParameter | DIFF | MATCH | fixed | outside a tool run: sys.argv |
| GetParameterAsText | DIFF | MATCH | fixed | outside a tool run: sys.argv |
| GetParameterCount | DIFF | MATCH | fixed | counts the named tool's parameters (Buffer 8, Clip 4) |
| GetParameterInfo | GRAPY-ERROR | MATCH | fixed | Parameter.datatype answers the type's name ("Double", "Linear Unit"); the keyword stays for the server |
| GetParameterValue | GRAPY-ERROR | MATCH | case | .pyt import |
| GetReturnCode | MATCH | MATCH | — | |
| GetSeverity | DIFF | DIFF | recorded | message model (open 1) |
| GetSeverityLevel | MATCH | MATCH | — | |
| GetSystemEnvironment | MATCH | MATCH | — | |
| GetUTMFromLocation | MATCH | MATCH | — | |
| ImportToolbox | GRAPY-ERROR | MATCH | case | .pyt import |
| Index | GRAPY-ERROR | MATCH | fixed | Index.fields are Field objects |
| InsertCursor | GRAPY-ERROR | MATCH | fixed | the lock (see Cursor) |
| IsBeingEdited | MATCH | MATCH | — | |
| IsSynchronous | MATCH | MATCH | — | |
| LinearUnitConversionFactor | MATCH | MATCH | — | |
| ListDatasets | DIFF | MATCH | fixed | a GeoPackage's feature datasets listed; only `*` is a wildcard |
| ListEnvironments | DIFF | DIFF | recorded | grapy holds 12 settings, Pro 75 (open 3) |
| ListFeatureClasses | MATCH | MATCH | — | |
| ListFields | DIFF | MATCH | case | compared on a GeoPackage |
| ListFiles | MATCH | MATCH | — | |
| ListIndexes | MATCH | DIFF | recorded | first run's match was the file geodatabase case failing alike; on a GeoPackage Pro lists FDO_OBJECTID and rtree_points_Shape besides the user index (open 4) |
| ListRasters | DIFF | MATCH | fixed | wildcards: `[..]` and `?` are not wildcards in Pro |
| ListTables | MATCH | MATCH | — | |
| ListToolboxes | DIFF | DIFF | fixed in part | 3D Analyst is "(3d)"; Pro lists 46 toolboxes, grapy its 13 (open 6) |
| ListTools | DIFF | DIFF | fixed in part | 3D tools are `<tool>_3d`; grapy lists sa/ia raster functions (Buffered_sa, Buffered_ia) as tools, and has 35 of Pro's 37 analysis tools (open 6) |
| ListVersions | PRO-ERROR | PRO-ERROR | not comparable | Pro: "Not a valid SDE workspace" — needs an enterprise geodatabase |
| ListWorkspaces | DIFF | DIFF | recorded | CreateFileGDB writes a .gpkg, so FileGDB finds 0 and SQLite 2 where Pro finds 1 and 1 (inherent, grapy's documented INFERRED choice) |
| Mensuration | MATCH | MATCH | not comparable in part | only the class constant is compared: it needs an image with RPCs, which the fixtures lack |
| Multipoint | GRAPY-ERROR | MATCH | fixed | hasCurves; getPart(i) is a Point; centroid/labelPoint = extent centre where the mean is no point of it |
| NetCDFFileProperties | DIFF | MATCH | fixed | coordinate variables are variables; a CF time ("… since …") is Date |
| NumPyArrayToRaster | MATCH | MATCH | fixed | (answer diff hidden by the harness) noDataValue is the value_to_nodata given, kept on save |
| Parameter | DIFF | MATCH | fixed | datatype names |
| ParseFieldName | DIFF | MATCH | fixed | in a GeoPackage a dot-qualified name raises, as Pro's |
| ParseTableName | DIFF | MATCH | fixed | as ParseFieldName |
| Point | BOTH-ERROR | MATCH | fixed | contains/within/disjoint/crosses/overlaps/touches on a Point |
| PointGeometry | GRAPY-ERROR | MATCH | fixed | hasCurves; buffer area compared to 0.5 % (Pro's true arcs) |
| Polygon | GRAPY-ERROR | MATCH | fixed | hullRectangle; labelPoint is the centroid when inside |
| Polyline | GRAPY-ERROR | MATCH | fixed | densify cuts each segment into floor(L/d)+1 pieces; line×line intersect in points; centroid/labelPoint = nearest point to the extent centre when the true centroid is off the line |
| RandomNumberGenerator | MATCH | MATCH | — | |
| Raster | GRAPY-ERROR | DIFF | fixed in part | format, compressionType, uncompressedSize; standardDeviation is the sample one (n − 1). Left: Pro's int32 zones raster hasRAT True, grapy False (open 7) |
| RasterToNumPyArray | REFUSED | MATCH | built | the window (lower_left_corner, ncols, nrows); a fill the cell type holds keeps it (float32) |
| RecordSet | GRAPY-ERROR | MATCH | fixed | a table's JSON has no geometryType |
| RemoveToolbox | GRAPY-ERROR | MATCH | case | .pyt import |
| ResetEnvironments | DIFF | DIFF | recorded | workspace default and XYTolerance not held (open 3) |
| ResetProgressor | DIFF | DIFF | recorded | answers None as Pro; the message counts after it differ (open 1) |
| Result | GRAPY-ERROR | DIFF | recorded | inputCount, maxSeverity, messageCount, getSeverity missing (open 1) |
| Row | DIFF | MATCH | fixed | the lock (see Cursor) |
| SearchCursor | MATCH | MATCH | — | |
| SearchNeighborhoodSmooth | DIFF | MATCH | fixed | nbrType and Pro's string form |
| SearchNeighborhoodSmoothCircular | DIFF | MATCH | fixed | as above |
| SearchNeighborhoodStandard | DIFF | MATCH | fixed | as above |
| SearchNeighborhoodStandard3D | BOTH-ERROR | MATCH | fixed | as above (case had one argument too many) |
| SearchNeighborhoodStandardCircular | DIFF | MATCH | fixed | as above |
| SetParameter | DIFF | MATCH | fixed | outside a tool run: does nothing |
| SetParameterAsText | DIFF | MATCH | fixed | as SetParameter |
| SetProgressor | DIFF | DIFF | recorded | as ResetProgressor |
| SetProgressorLabel | DIFF | DIFF | recorded | as ResetProgressor |
| SetProgressorPosition | DIFF | DIFF | recorded | as ResetProgressor |
| SetSeverityLevel | REFUSED | DIFF | recorded | levels 0 and 1 stay refused (open 2) |
| SeverityLevel | DIFF | MATCH | fixed | the block puts the previous level back |
| SpatialReference | GRAPY-ERROR | MATCH | fixed | Esri names (WGS_1984_UTM_Zone_35N), projection parameters, GCS, datum/spheroid, tolerances; exportToString is Esri WKT |
| TestSchemaLock | MATCH | MATCH | — | |
| UpdateCursor | GRAPY-ERROR | MATCH | fixed | the lock (see Cursor) |
| Usage | GRAPY-ERROR | DIFF | recorded | Pro writes a system tool's keyword choices ({Full \| Left \| …}), Esri's catalogue; the .pyt tool matches |
| ValidateFieldName | DIFF | MATCH | fixed | GeoPackage: "1st"→"F1st", SQL keywords get "_", non-ASCII letters kept, "" stays ""; folder: leading digit gets "a" |
| ValidateTableName | DIFF | MATCH | fixed | GeoPackage: "T1st"; folder: only spaces replaced |
| Value | PRO-ERROR | DIFF | recorded | Pro's Value is not constructible; a GPLinearUnit parameter's value is a "geoprocessing value object" in Pro, a str in grapy (str() equal) |
| ValueTable | BOTH-ERROR | MATCH | fixed | getRow quotes every value ("'a' '1'"); case dropped getTrimmedValue/setColumnNames, which Pro lacks |

## Open differences worth a debt row

1. **The message model** (AddMessage, AddWarning, AddError, AddIDMessage, AddReturnMessage, the Get*Message* family,
   GetMaxSeverity, GetSeverity, Command, Result.inputCount/maxSeverity/messageCount/getSeverity, the progressor names'
   counts). Pro answers the last tool's messages — "Start Time…", the tool's own ("Row Count = 30"), "Succeeded at…",
   with GetAllMessages types 2/0/3 — and AddMessage outside a tool prints without joining them; grapy keeps one list for
   the whole run, its tools write none of the start/end messages, and AddMessage appends to it. Changing it means every
   tool's call is wrapped (ADR-223's message list is what the server's job answers). Estimate: 1–2 days.
2. **SetSeverityLevel 0 and 1** stay refused: every tool would have to consult the level. Estimate: half a day once (1)
   exists.
3. **Environment defaults and the environments held**: Pro's ClearEnvironment/ResetEnvironments leave workspace None;
   grapy's default is the run's directory by its documented INFERRED choice for the fenced server (ADR-223 §5.5) — left,
   needs an owner decision. grapy holds 12 of Pro's 75 environments (XYTolerance, parallelProcessingFactor, cellAlignment…
   are not held, so ResetEnvironments does not reset them).
4. **GeoPackage column names**: grapy writes `fid`/`geom`, Pro writes `OBJECTID`/`Shape` even in a GeoPackage
   (Describe's OIDFieldName/shapeFieldName/fields, da.SearchCursor "*", ListIndexes' FDO_OBJECTID and
   rtree_<t>_Shape). Changing grapy's columns touches every reader. Estimate: days.
5. **A GeoPackage holding one table** is described as that table by grapy's convention; Pro describes it as a Workspace.
6. **ListToolboxes/ListTools**: grapy lists its 13 modules and every capitalised function, including raster functions
   that are not tools (sa.Buffered, ia.Buffered…); Pro lists 46 toolboxes.
7. **Raster attribute table**: Pro answers hasRAT True for the sweep's int32 zones raster (made by NumPyArrayToRaster
   then DefineProjection); grapy builds no table there.
8. **Usage of a system tool** writes Esri's keyword display choices, which grapy does not hold.
9. **Harness**: compare.py skips result.json when a case writes outputs (see the note above).
