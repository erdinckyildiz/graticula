# arcpy.conversion against ArcGIS Pro 3.7 — 2026-11-08

All 32 names grapy builds, one case each (`cases/conversion.py`). A tool that writes a file format both sides can read
(shapefile, dBASE, Esri JSON, GPX, ASCII and float grids, Excel, Parquet, LAS, COLLADA) is compared by reading the file
back in the case with the standard library (pyarrow for Parquet) and comparing what was read; what only the input's
storage decides (the ObjectID's name, the shape column's name, a geodatabase's Shape_Length/Shape_Area) is left out,
since the shared fixtures are a file geodatabase on Pro's side and a GeoPackage on grapy's. Inputs beyond the shared
fixtures — a grid file, a GPX, a KML, GTFS text, a workbook, a LAS 1.2 file, an OBJ box imported as a multipatch — are
written in the case, the same bytes on both sides.

**First run: MATCH 17, DIFF 14, PRO-ERROR 1. Now: MATCH 31, DIFF 1 (KMLToLayer, recorded).**

Before that first run two cases were wrong for Pro and were fixed: ConvertLas (Pro needs
`define_coordinate_system="FILES_MISSING_PROJECTION"` with `in_coordinate_system` for a LAS file with no spatial
reference — the first run still had it PRO-ERROR), and ExportToParquet (see below). Several first-run DIFFs were the
case comparing storage names (FeaturesToJSON, ExportToParquet, TableToExcel, TableToGeodatabase's shape type); those
were fixed in the case, not in grapy, and are marked *case*.

The fixes are pinned in `src/grapy/tests/test_pro_sweep_conversion.py`; tests that pinned the old answers
(`test_round5.py` GTFS `location_type`, `test_management_round3.py` KML `Lines`, and the raster grids in
`test_conversion_raster.py`, `test_lookagain.py` (MultipatchToRaster) and `test_sweep.py` (PolylineToRaster)) were
changed with a comment saying Pro was measured.

| Name | First run | Now | Action | Note |
|---|---|---|---|---|
| ASCIIToRaster | MATCH | MATCH | — | |
| ConvertLas | PRO-ERROR | MATCH | — | case fixed (spatial reference); version, point format, record length, count and bounds match. Pro also writes a `.lasx` index, grapy does not. |
| ExcelToTable | MATCH | MATCH | — | workbook written by the case (inline strings). |
| ExportFeatures | MATCH | MATCH | — | |
| ExportTable | MATCH | MATCH | — | |
| ExportToParquet | DIFF | MATCH | case | the difference was storage only (OBJECTID/Shape/Shape_Length/Shape_Area columns of the gdb input against fid/geom of the GeoPackage input; Pro's OID int32, grapy's int64 as its input's). **Pro defect, recorded:** Pro 3.7 writes no file and still reports success — "General function failure" for every feature — when the output path is longer than about 130 characters (126 wrote, 136 did not); a table input fails with ERROR 999999. The case writes to a short folder of the system temp directory for that reason. |
| FeatureClassToFeatureClass | MATCH | MATCH | — | |
| FeatureClassToGeodatabase | MATCH | MATCH | — | |
| FeatureClassToShapefile | DIFF | MATCH | fixed | read back (.shp header, .shx count, .dbf): Pro writes doubles as `F(19,11)` and text as `C(254)` (the field's length, cut to 254); grapy wrote `N(19,11)` and text as wide as its longest value. Pro's own Shape_Leng/Shape_Area columns come from the gdb input and are left out. |
| FeatureToRaster | DIFF | MATCH | fixed | as PolygonToRaster. |
| FeaturesToGPX | MATCH | MATCH | — | read back. |
| FeaturesToJSON | DIFF | MATCH | case | read back; only the OID field's name differed. |
| FloatToRaster | MATCH | MATCH | — | |
| GPXtoFeatures | MATCH | MATCH | — | |
| GTFSShapesToFeatures | MATCH | MATCH | — | |
| GTFSStopsToFeatures | DIFF | MATCH | aligned | Pro writes `location_type` and `wheelchair_boarding` as text; grapy's INFERRED short integers. |
| JSONToFeatures | MATCH | MATCH | — | |
| KMLToLayer | DIFF | DIFF | partly fixed, recorded | Pro names the line class `Polylines` (grapy `Lines` — fixed). Still different: Pro writes `SymbolID`, `AltMode`, `Base`, `Snippet`, `HasLabel`, `LabelID` (points) and `Clamped`, `Extruded` (lines, polygons), and a `.lyrx` beside the workspace; grapy writes `Name`, `FolderPath`, `PopupInfo` only and no layer file. The extra fields are undocumented (values measured: well → SymbolID 0, AltMode −1, Base 0, Snippet '', HasLabel −1, LabelID 0; polygon → AltMode 0, Clamped −1, Extruded 0). |
| LasDatasetToRaster | DIFF | MATCH | fixed | binning: a point on the line between two rows falls in the row **above** (a cell holds its bottom edge); grapy put it in the row below. Measured over 900 cells (881 differed). The shared helper also serves LasPointStatsAsRaster. |
| MultipatchToCollada | MATCH | MATCH | — | read back: file kinds, COLLADA version, up axis, model span. |
| MultipatchToRaster | DIFF | MATCH | fixed | without a snap raster the grid is the extent widened by half a cell on every side, laid from its lower-left corner (a 40 m box at 10 m is 5 cells); grapy laid the bare extent from its top-left (4 cells). One measurement. |
| PointToRaster | DIFF | MATCH | fixed | the grid is the points' extent widened by half a cell on every side (100 m → 11 cells, 95 m → 11), from its lower-left; a point on a cell edge falls in the cell left of it and above it. |
| PolygonToRaster | DIFF | MATCH | fixed | the grid is laid from the extent's lower-left corner (grapy: top-left); where polygons overlap the **first** feature's value is kept (measured both ways round; grapy kept the last). |
| PolylineToRaster | DIFF | MATCH | fixed | the grid is laid from the lower-left corner with one cell more than fit whole (100 m → 11 cells, 95 m → 10); a line lying on a cell edge belongs to the cell left of it or above it, the grid's own edges keeping theirs (measured cell for cell with six lines). |
| RasterToASCII | MATCH | MATCH | — | read back. |
| RasterToFloat | MATCH | MATCH | — | read back (.hdr and .flt). |
| RasterToPoint | MATCH | MATCH | — | a 6 × 6 integer raster with a NoData cell, built in the case. |
| RasterToPolygon | MATCH | MATCH | — | with `NO_SIMPLIFY`; `SIMPLIFY` (the default) is refused by grapy, not compared. |
| TableToDBASE | DIFF | MATCH | fixed | as FeatureClassToShapefile's .dbf. |
| TableToExcel | DIFF | MATCH | case | read back; only the OID column's header differed. |
| TableToGeodatabase | DIFF | MATCH | case | grapy's `Describe` of a table answers shapeType `Geometry`, Pro's none — an arcpy.Describe difference (see below), left out of this case. |
| TableToTable | MATCH | MATCH | — | |

## What else carries these

- The three features-to-raster grid rules are one helper (`conversion._conversion_grid`) used by PolygonToRaster,
  FeatureToRaster, PointToRaster and PolylineToRaster; `sa._grid_over` (the old top-left rule) is still used by
  arcpy.sa tools (not measured here, not changed). MultipatchToRaster lays its own grid and was changed to the points
  rule.
- The edge rule is one helper (`conversion._edge_cell`) for points, and the same rule written into PolylineToRaster's
  cell test. `_raster._Grid.cell_of` (used widely elsewhere) still puts a point on an edge in the cell right of it and
  below it — unchanged, since its other users were not measured.
- The LAS row rule is `_las._cells`, which LasDatasetToRaster and LasPointStatsAsRaster share.
- The dBASE layout is `conversion._dbf_layout`, shared by FeatureClassToShapefile, TableToDBASE and
  `_topo_prod_schema`'s shapefile export.

## Outside these names

- grapy's `arcpy.Describe` answers `shapeType` "Geometry" for a table; Pro's Describe of a table has no shape type.
- grapy's `arcpy.da.Walk` names a class in a feature dataset `Placemarks/Points`; Pro's Walk gives `Points` with the
  dataset in the root.

## Open differences worth a debt row

- KMLToLayer: the eight undocumented KML fields and the `.lyrx` layer file (see the table). The fields' meanings would
  have to be measured placemark by placemark (altitude modes, extrude, styles, labels) — about a day; the layer file
  depends on grapy having a layer-file writer.
- PointToRaster and PolylineToRaster edge rule measured on few features (one point, six lines); LasDatasetToRaster's
  binning puts a point on a column edge in the column to the **right**, unlike the feature tools' left — both measured,
  both kept as measured.
- MultipatchToRaster's grid rule rests on one measurement.
- Pro 3.7 ExportToParquet's silent failure on long output paths is Pro's defect, not grapy's — worth knowing for anyone
  comparing the two.
