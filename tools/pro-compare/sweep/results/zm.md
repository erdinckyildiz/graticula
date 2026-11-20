# Z and M through the general tools against ArcGIS Pro 3.7 — 2026-11-20 (D-412)

Every earlier module used 2D fixtures, so none saw that grapy's analysis, management and conversion tools wrote 2D
outputs from 3D inputs (ddd.md 1). `cases/zm.py` feeds 50 tools PolylineZM, PolygonZM and PointZM inputs (and a 2D
polygon as the other side of the overlays): 81 cases, each answering the output's `[hasZ, hasM]` and dumping its
shapes, which compare.py now compares with z **and m** (`_m_distance`, added for this module; z and m are both read
along each part's lines and rings apart, so a vertex where two parts meet is matched against either part's value).

**First run (grapy at c4bc8b60): MATCH 9, DIFF 65, GRAPY-ERROR 6, REFUSED 1. Now: MATCH 78, DIFF 1, SCHEMA 1,
REFUSED 1.** The fixes are pinned in `src/grapy/tests/test_zm_through_tools.py`.

## What Pro does (the rule grapy now follows, `grapy/_zm.py`)

1. An output carries z when an input carries z and m when an input carries m — the `outputZFlag` and `outputMFlag`
   environments' default, *Same As Input*. Any input counts: Clip, Erase and Intersect of a 2D polygon by a PolygonZM
   write PolygonZM (z 0, m NaN on the 2D input's own vertices). `Enabled`/`Disabled` decide instead: Clip under
   `outputZFlag = "Disabled"` writes a PolylineM, CopyFeatures of a 2D class under `outputMFlag = "Enabled"` a
   PolygonM. Pro accepts any text in those environments and reads `ENABLED`/`DISABLED` back capitalised.
2. **Buffer, PairwiseBuffer, GraphicBuffer and MultipleRingBuffer write 2D polygons** whatever their input.
3. A tool that cuts an input's shapes keeps each vertex's own z and m, and a new vertex on an input's segment takes the
   values interpolated along it by 2D distance (Clip at x = 150 of the segment (0, z 100, m 0)–(200, z 120, m 3):
   z 115, m 2.25). A vertex on a 3D input's edge takes that input's values even in a piece that input does not own
   (Union's and SymDiff's piece of the 2D polygon alone); a vertex on no 3D input has z 0 and m NaN. Where two input
   lines cross, each piece keeps its own line's z (Feature To Line, Dissolve: 20 on one line, 15 on the other).
4. A tool that makes a shape rather than cutting one writes z 0 and m NaN: Feature To Point's centre (even where it
   lies on an input vertex), Minimum Bounding Geometry's envelope, Create Thiessen Polygons.

grapy follows these through `_gpkg.create`: a call that does not say has_z/has_m now follows the output environments
over the running tool's input parameters (`_zm.from_tool`, read from the tool's own call by `_runtime.run_tool`),
which reaches every tool that writes features; tables never take z. The cutting tools restore z and m from their
inputs (`_zm.restore`): Clip, Erase, the planar and pairwise overlays (Intersect, Union, Identity, SymDiff and the
Pairwise ones), Update, Split, Dissolve and PairwiseDissolve, Feature To Line, Feature To Polygon, Polygon To Line,
Split Line, Split Line At Point, Feature Vertices To Points, Generate Points Along Lines, Points To Line,
Count Overlapping Features, Eliminate, SimplifyLine and edit.Densify.

**Crashes fixed on the way:** Feature Vertices To Points on a polygon with m, Split Line and Split Line At Point on
anything with m raised shapely's "ordinate dimension should be 2 or 3, got 4".

## Not changed, recorded

- **A ring's closing vertex.** Pro stores a ring's end as its start: a polygon written with a closing m of 4 and a
  first m of 0 reads back 0 at both. grapy keeps the 4. The fixture now closes its rings on the first m.
- **Minimum Bounding Geometry ENVELOPE of a horizontal line** — Pro writes a 2 m tall envelope (y ± 1), grapy a
  zero-height one. A 2D difference, one measurement; not copied.
- **Spatial Join** carries the join features' `Shape_Length` (here `Shape_Length_1`) and `Shape_Area` as ordinary
  fields in Pro; grapy's join features in a GeoPackage have no such fields. 2D, recorded.
- **Densify by distance** — Pro cut a 200 m segment at "50 Meters" into five pieces of 40 m, grapy into four of 50
  (z and m interpolated on both). One measurement; recorded beside Densify's INFERRED note.
- **Intersect and Identity of lines** cut a line where another line of the same input crosses it in Pro (a vertex
  at the crossing, the piece in two parts); grapy keeps the line whole. Same shape, z and m; recorded.
- **SmoothLine** stays refused (PAEK's kernel is not specified).

## Cases

| Tool | First run | Now |
|---|---|---|
| analysis.Buffer | lines MATCH; points MATCH | lines MATCH; points MATCH |
| analysis.Clip | flat_by_3d DIFF; lines DIFF; lines_into_2d GRAPY-ERROR; points DIFF; polys DIFF | flat_by_3d MATCH; lines MATCH; lines_into_2d MATCH; points MATCH; polys MATCH |
| analysis.CountOverlappingFeatures | polys DIFF | polys MATCH |
| analysis.CreateThiessenPolygons | points DIFF | points MATCH |
| analysis.Erase | flat_by_3d DIFF; lines DIFF; points DIFF; polys DIFF | flat_by_3d MATCH; lines MATCH; points MATCH; polys MATCH |
| analysis.GraphicBuffer | lines MATCH | lines MATCH |
| analysis.Identity | lines DIFF; polys DIFF | lines MATCH; polys MATCH |
| analysis.Intersect | flat_first DIFF; lines DIFF; points DIFF; polys DIFF | flat_first MATCH; lines MATCH; points MATCH; polys MATCH |
| analysis.MultipleRingBuffer | points MATCH | points MATCH |
| analysis.PairwiseBuffer | lines MATCH | lines MATCH |
| analysis.PairwiseClip | lines DIFF; polys DIFF | lines MATCH; polys MATCH |
| analysis.PairwiseDissolve | lines DIFF; polys DIFF | lines MATCH; polys MATCH |
| analysis.PairwiseErase | lines DIFF; polys DIFF | lines MATCH; polys MATCH |
| analysis.PairwiseIntersect | lines DIFF; polys DIFF | lines MATCH; polys MATCH |
| analysis.Select | lines DIFF; points DIFF; polys DIFF | lines MATCH; points MATCH; polys MATCH |
| analysis.SpatialJoin | lines DIFF; points DIFF | lines SCHEMA; points MATCH |
| analysis.SymDiff | polys DIFF | polys MATCH |
| analysis.Union | polys DIFF | polys MATCH |
| analysis.Update | polys DIFF | polys MATCH |
| cartography.SimplifyLine | lines DIFF | lines MATCH |
| cartography.SmoothLine | lines REFUSED | lines REFUSED |
| conversion.FeatureClassToFeatureClass | lines DIFF; points DIFF | lines MATCH; points MATCH |
| management.Append | lines MATCH | lines MATCH |
| management.CopyFeatures | flat_m_enabled GRAPY-ERROR; lines MATCH; points MATCH; polys MATCH | flat_m_enabled MATCH; lines MATCH; points MATCH; polys MATCH |
| management.Densify | lines DIFF | lines MATCH |
| management.Dissolve | lines DIFF; polys DIFF | lines MATCH; polys MATCH |
| management.Eliminate | polys DIFF | polys MATCH |
| management.ExportFeatures | lines DIFF; points DIFF; polys DIFF | lines MATCH; points MATCH; polys MATCH |
| management.FeatureEnvelopeToPolygon | lines DIFF; polys DIFF | lines MATCH; polys MATCH |
| management.FeatureToLine | lines DIFF; polys DIFF | lines MATCH; polys MATCH |
| management.FeatureToPoint | lines DIFF; polys DIFF | lines MATCH; polys MATCH |
| management.FeatureToPolygon | polys DIFF | polys MATCH |
| management.FeatureVerticesToPoints | lines DIFF; polys GRAPY-ERROR | lines MATCH; polys MATCH |
| management.GeneratePointsAlongLines | lines DIFF | lines MATCH |
| management.Merge | lines DIFF; points DIFF; polys DIFF | lines MATCH; points MATCH; polys MATCH |
| management.MinimumBoundingGeometry | lines DIFF; polys DIFF | lines DIFF; polys MATCH |
| management.MultipartToSinglepart | lines DIFF; polys DIFF | lines MATCH; polys MATCH |
| management.PointsToLine | points DIFF | points MATCH |
| management.PolygonToLine | polys DIFF | polys MATCH |
| management.Project | lines DIFF; points DIFF | lines MATCH; points MATCH |
| management.Sort | lines DIFF; points DIFF; polys DIFF | lines MATCH; points MATCH; polys MATCH |
| management.SplitLine | lines GRAPY-ERROR; polys GRAPY-ERROR | lines MATCH; polys MATCH |
| management.SplitLineAtPoint | lines GRAPY-ERROR | lines MATCH |
