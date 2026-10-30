# ADR-229 — grapy holds LAS datasets and TINs

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` for the model · `HIGH` for the TIN measurements, which are exact on a triangle's plane · `LOW` for agreement with Pro's triangulation wherever breaklines force densification points, whose placement Esri does not publish |
| **Decided** | 2026-10-30, by owner decision on scope and order: LAS datasets and TINs are their own ADR and batch, after subtypes and attachments (ADR-228, written beside this one) and before mosaic datasets and the utility network ([ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.7). The storage design below is INFERRED and listed for confirmation in §5.9 and [Q-196](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Before this decision every 3D Analyst tool on a TIN, a LAS dataset or a terrain was refused with *"needs a data model
this server does not hold yet"*: about fifty names in `arcpy.ddd`, nineteen in `arcpy.management` that named LAS datasets or TINs among
other 3D models, `arcpy.conversion.LasDatasetToRaster`, and `arcpy.ListLasDatasetFiles`. A user tool that turns a lidar
survey into a ground model, or measures a stockpile's volume, could not run on this server.

What ArcGIS publishes, and what it does not:

- **LAS and LAZ are public formats.** LAS is the ASPRS specification (1.0–1.4); LAZ is LASzip's open compression.
  Esri's own **zLAS is not published.**
- **A .lasd is not documented.** The only description is *"This .lasd file is a pointer to .las files and surface
  constraints … It only stores references to .las files and surface constraints."* Its companions — .lasx statistics,
  the .slas pyramid — are not documented either.
- **A TIN's on-disk format is not documented.** The pages say a TIN is a dataset with a CURRENT or PRE_10.0 storage
  version, and nothing about its files.
- **What a TIN *is* is documented well**: Delaunay triangulation, mass points as nodes, breaklines *"enforced in the
  triangulation as a series of triangle edges. Steiner, or densification, points are added as needed to maintain
  conformance to Delaunay criteria"*, clip and erase polygons masking the data area, replace polygons flattening it,
  value fill polygons tagging triangles, hard and soft edges, and linear interpolation that *"views TIN triangles as
  planes"*. Each tool page names its output fields.
- **A terrain dataset** is a multiresolution pyramid kept inside a geodatabase feature dataset and built by Esri's
  thinning; its pages describe managing the pyramid, not a result a tool could be checked against.

Every page cited here was read on pro.arcgis.com on 2026-10-30, by an agent that fetched them and quoted them; no Esri
implementation was read (CLAUDE.md §5).

## 2. Alternatives considered

### Alternative A — grapy's own TIN tables in the GeoPackage, a JSON .lasd, LAS through laspy (chosen)

A TIN is grapy's tables in the GeoPackage a tool works on, as ADR-225, ADR-226 and ADR-227 hold their models. A LAS
dataset is a small JSON file pointing at LAS files, which is all a .lasd is documented to be. LAS files are read and
written with laspy (BSD-2-Clause, pure Python over numpy). Triangulation is scipy's Delaunay (Qhull), already in the
image. **For:** one new wheel, no Esri format reproduced, every tool computable from its page. **Against:** a TIN or
.lasd made by ArcGIS Pro cannot be opened, and one made by grapy cannot be opened by Pro.

### Alternative B — read and write Esri's TIN folder and .lasd

**For:** interchange with Pro. **Against:** both formats are unpublished; reading them means reverse-engineering
Esri's files, which the clean room forbids (CLAUDE.md §5). Rejected.

### Alternative C — PDAL for LAS, CGAL or Triangle for constrained triangulation

**For:** PDAL is the reference point-cloud library, and CGAL and Shewchuk's Triangle give true constrained Delaunay.
**Against:** PDAL is a large native dependency for what laspy and numpy do here; CGAL's triangulation packages are
GPL, and Triangle's licence forbids commercial use — neither may enter the image (ADR-047, DEPENDENCY-LICENSES.md). A
permissive constrained triangulator would be a new decision (condition 6).

### Alternative D — refuse group 2's 3D models

Rejected by the owner's scope answer.

## 3. Counterarguments to the preferred option

**Densification points differ from Pro's.** The documentation says breaklines are densified to stay Delaunay but not
where the points go. grapy splits a missing segment at its midpoint and triangulates again (§5.3). A TIN with
breaklines therefore has different nodes and triangles from Pro's; interpolated heights agree wherever both TINs share
the same triangles, and along the breakline itself, where both are linear between its vertices. The tests check the
documented properties — the hard edges are exactly the breakline, every triangle has an empty circumcircle — rather
than Pro's node count.

**A .lasd is not interchangeable.** A toolbox that ships a .lasd made in Pro will fail by name, and the user must make
one with `CreateLasDataset` in the tool. That is one line in a script, and the alternative is the clean room.

## 4. Evidence

The TIN measurements are exact, not approximations: on a plane, the part of a triangle on one side of a horizontal
plane is a convex polygon, and a linear function's integral over any polygon is its area times the function's value at
the centroid. The tests check them against hand integration — 1000 under z = 20 for the plane z = x + y over a 10 m
square, 166.67 above z = 10, 125 over the 5 m corner. No performance claim is made: a user tool runs under ADR-223's
limits, and a TIN too large for them fails by those limits. Densification is capped at ten times the input nodes and
fails by name past it (segments meeting at very small angles are the known case).

## 5. Decision

1. **grapy holds TINs and LAS datasets, not terrains.** Group 2's 3D models begin with these two because the tools on
   them are documented by their results.
2. **Storage, INFERRED (§5.9).**
   - A TIN is five tables registered as the `grapy_tin` extension in `gpkg_extensions`: `grapy_tin` (name, reference,
     the delineation last applied), `grapy_tin_input` (each surface feature added, in order, with its type, tag,
     height and geometry, z resolved), `grapy_tin_node` (x, y, z, tag, and origin — input or densification),
     `grapy_tin_triangle` (three nodes, tag, inside the data area or not) and `grapy_tin_edge` (the enforced edges, soft
     1 or hard 2). The triangulation is derived from the inputs, so EditTin triangulates them all again.
   - A TIN path is `<file>.gpkg/<name>`, or a folder path as a script written for Pro gives one, held as
     `<folder>/tin.gpkg` and named after the folder.
   - A .lasd is grapy's JSON: the files (absolute or relative), each file's statistics with the size and time of the
     file they describe, the surface constraints, the spatial reference. A .lasd that is not grapy's is refused by name.
3. **Triangulation is Delaunay conforming to its breaklines.** A breakline segment that is not an edge of the
   triangulation is split at its midpoint and the points are triangulated again, until every piece is an edge; where
   breaklines cross, the crossing becomes a node. **CONSTRAINED_DELAUNAY is refused**: scipy has no constrained
   triangulation, and the permissive alternatives are not chosen (condition 6). LandXMLToTin, whose page says it makes
   a constrained TIN, and SurfaceDifference and TinDifference, whose difference surface is constrained, are refused
   with that reason.
4. **Interpolation on a TIN is linear**, on each triangle's plane, as documented. Natural neighbours and the conflation
   methods are refused by name until a natural neighbour interpolant over a TIN is built.
5. **LAS through laspy; LAZ only when a LAZ backend is installed, and none is in the image yet (condition 5); zLAS is
   refused** — Esri's format is not published.
6. **Terrain datasets are refused**, every tool naming them: a terrain is a pyramid inside a geodatabase feature
   dataset, built by Esri's thinning, and a TIN or LAS dataset is the surface grapy holds.
7. **What the pages leave undefined is refused or an equivalent (ADR-224 §5.6).** DecimateTinNodes and RasterTin do not
   say how points are chosen; RANDOM thinning does not say how points are drawn — refused. ClassifyLasGround's methods
   are not described; it is built as an **equivalent**, the progressive morphological filter (Zhang et al. 2003),
   marked and counted apart, with the method's slope for STANDARD, CONSERVATIVE and AGGRESSIVE INFERRED.
8. **`arcpy.ListLasDatasetFiles` is refused as undocumented**: it is in `dir(arcpy)` but absent from ArcPy's function
   list, so its parameters and answer are unknown.
9. **INFERRED, for confirmation** ([Q-196](../open-questions.md)):
   - (a) the TIN tables, the folder path form, and the JSON .lasd of §5.2;
   - (b) densification at segment midpoints, and of two inputs at one x,y the later one's z kept — so a LAS
     dataset's TIN keeps the last-written of two returns at one position;
   - (c) a triangle is inside a clip, erase, replace or value fill polygon when its centroid is; all clips apply
     before any erase; a replace polygon's height is its height field, or its first vertex's z;
   - (d) a plain .lasd shows every point but the withheld ones, as a new LAS dataset layer's defaults do;
   - (e) raster grids: TinRaster's lower-left cell centred on the TIN's corner, as documented, with whole cells to the
     upper right; the LAS rasters' grid starting at the dataset's lower-left point, cell size 10 by default;
   - (f) report and field names the pages do not give: SurfaceVolume's header, LasDatasetStatistics' columns,
     `SlopeCode`, `AspectCode`, `Contour`, `Hillshade`;
   - (g) ClassifyLasGround's filter parameters (slopes 0.15 / 0.3 / 0.6, a 0.3 m base threshold, windows to 20 m,
     cell 1.5 × the point spacing);
   - (h) LASToMultipoint writes one multipoint per file, the page saying only that recursion gives "a row for each
     file".
   - (i) **the second batch's LAS outputs** (ConvertLas, ProjectLAS, ColorizeLas, ThinLas, TileLas): a file keeps its
     source's name with `.las` (ConvertLas's page does not name its outputs); REARRANGE_POINTS keeps the points' order;
     REMOVE_VLR keeps only the coordinate system's records; with no format given, the source's point format, or its
     counterpart across the 1.4 divide (6↔1, 7/8↔3, 9↔4, 10↔5); a format must hold the input's class codes and flags,
     as ConvertLas documents, applied to every tool that converts;
   - (j) ProjectLAS: PROJ's default transformation, an Esri transformation name refused; z transformed only where both
     systems have a vertical datum; output scale 0.0000001 for geographic x and y, else 0.001, offsets the whole units
     below the smallest coordinates;
   - (k) ColorizeLas: a point takes the image cell it falls in, unresampled and unscaled (rounded, held to 0–65535),
     0 off the image or on NoData; a channel not named keeps the source's value; every point of a file is coloured;
   - (l) ThinLas: regions start at the file's lower-left corner and lowest z; CLOSEST_TO_CENTER is 2D for 2D thinning
     and 3D for 3D; an unweighted class weighs 0; PREDOMINANT_CLASS keeps the point of the predominant class closest to
     the centre; other ties go to the earlier point; the layer's filters are honoured;
   - (m) TileLas: a point on a tile's left or bottom edge belongs to it; a megabyte is 2^20 bytes in the file-size
     estimate; the base name defaults to the LAS dataset's; ROW_COLUMN counts from 1 at the origin; extra bytes are
     kept where every file has them; the first file's coordinate system, records and scale;
   - (n) ClassifyLasOverlap: the squares start at the file's lower-left corner; "closest to nadir" is the smallest
     absolute scan angle, a tie to the lower point source ID; the layer's filters are not read; formats 9 and 10 take
     the overlap flag as 6–8 do;
   - (o) ClassifyLasBuilding, an **equivalent** (Rabbani et al. 2006, region growing with a smoothness constraint):
     tolerances CONSERVATIVE 0.05, STANDARD 0.1, AGGRESSIVE 0.2; neighbours within 2 × the point spacing; planes no
     steeper than 60° and normals within 15°; a rooftop's area its points' convex hull; `min_height` 2 by default;
   - (p) LocateLasPointsByProximity: the radius is 3D and inclusive; MULTIPOINT is the default and writes one
     multipoint per feature; POINT writes each point once with its distance to the nearest feature;
   - (q) LocateOutliers: natural neighbours are the Delaunay edges; a neighbour exceeds the tolerances when the height
     difference *and* the slope both exceed theirs; the cap takes points in node order;
   - (r) InterpolateFromPointCloud: TRIANGULATION, no smoothing and DSM by default; withheld points left out; the grid
     at the points' lower-left corner; a DTM only from ground already classified (ReuseGround 1).
     ExtractObjectsFromPointCloud: a code without a group ID is its own group, the centre point the points' mean,
     BOUNDING_BOX_2D axis-aligned, the concave hull the alpha shape of the Delaunay triangles whose circumradius is at
     most the radius.

## 6. Consequences

- **State.** In the GeoPackage the tool works on, and in the .lasd and LAS files in its run directory. Nothing in the
  catalogue or the server.
- **The image gains one wheel**, laspy (BSD-2-Clause, pure Python). DEPENDENCY-LICENSES.md's grapy section lists it;
  the Dockerfile and both CI jobs install it.
- **Names.** `tools/grapy-refusals.json` before → after: `arcpy.ddd` built 35 → 62 (equivalents 5 → 6), refused
  121 → 94; `arcpy.management` built 149 → 156, refused 298 → 291; `arcpy.conversion` built 27 → 28, refused
  37 → 36; none unexamined. The second batch (condition 4): `arcpy.ddd` built 62 → 70 (equivalents 6 → 7), refused
  94 → 86; `arcpy.management` built 190 → 192, refused 257 → 255; `arcpy.conversion` built 28 → 29, refused 36 → 35.
- **ChangeLasClassCodes and the other classifiers rewrite the LAS files in place**, as documented: the files a run
  reaches are the ones in its own directory (ADR-223 §5.5).

## 7. Assumptions this decision rests on

- A user tool's lidar fits in its run's memory: laspy reads a whole file at once, and a TIN is triangulated in memory.
  Nothing has been measured; a file too large fails by ADR-223's limits, by name.
- Qhull's Delaunay is correct for the inputs it accepts; nodes it drops as coincident are deduplicated before.

## 8. Dependencies

[ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.6–5.7,
[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) (the fence and limits),
[ADR-225](ADR-225-grapy-holds-topology-and-relationship-classes.md) (the `gpkg_extensions` registration it reuses),
laspy, scipy, shapely; clean room (CLAUDE.md §5): only documented behaviour is reproduced, and no Esri file format.

## 9. Revisit triggers

- A permissively licensed constrained Delaunay triangulator becomes available as a wheel: CONSTRAINED_DELAUNAY,
  LandXMLToTin and SurfaceDifference become buildable.
- A user needs to open a TIN or .lasd made by ArcGIS Pro — the answer is still no, but the request is worth recording.
- Point clouds beyond a run's memory become a real use; laspy's chunked reader is then the step.

## 10. Dissent

None recorded.

## 11. Conditions

1. **CreateTin and EditTin** build a conforming Delaunay TIN from mass points, hard and soft lines and clip, erase,
   replace and value fill polygons, with `<None>` heights interpolated, tested on hand-drawn data. **DISCHARGED
   2026-10-30**: `test_tin.py` checks that a hard breakline across 64 random points comes back as hard edges that are
   exactly the line, every triangle with an empty circumcircle, its densification points at the line's height; that
   a hard clip and soft erase leave 32 m² of data area with the clip's `<None>` corners on the plane; that a replace
   polygon flattens its nodes to 3.5 and a value fill tags 9 m².
2. **The TIN tools answer hand-worked values**: TinNode, TinEdge, TinTriangle, TinLine, TinPolygonTag, TinDomain,
   TinRaster, SurfaceContour and TinContour, SurfaceSlope, SurfaceAspect, SurfaceVolume, PolygonVolume and
   TinPolygonVolume, DelineateTinDataArea, CopyTin, MakeTinLayer, InterpolateShape and UpdateFeatureZ on a TIN, and
   Describe. **DISCHARGED 2026-10-30**: twenty tests on the plane z = x + y and a two-cluster TIN — slope 141.42 %,
   aspect 225°, hillshade 104, contours of length 5√2 and 10√2, volumes 1000, 166.67 and 125, two unit squares of
   data area left by a 2 m edge limit.
3. **LAS datasets**: CreateLasDataset, AddFilesToLasDataset, RemoveFilesFromLasDataset, LasDatasetStatistics,
   MakeLasDatasetLayer and its filters, LasPointStatsAsRaster, LasDatasetToRaster, LasDatasetToTin, LASToMultipoint,
   PointFileInformation, LasPointStatsByArea, ExtractLas, ChangeLasClassCodes, SetLasClassCodesUsingFeatures,
   ClassifyLasByHeight and ClassifyLasGround (an equivalent), and Describe, on LAS files the tests write. **DISCHARGED
   2026-10-30**: `test_las.py` builds a 20 m scene with a 4 m roof of two-return points and a withheld point; the
   filters, counts, binned and triangulated rasters, roof statistics (6.25 mean, 0.25 deviation), class edits,
   height classes and the morphological filter (every ground point but the withheld one, no roof point) are checked
   against it.
4. **Every remaining TIN, LAS and terrain name is built or refused with its own reason.** **DISCHARGED
   2026-10-30**: none answers the generic message, and terrains, constrained triangulation, undocumented selection,
   multipatch outputs and deep learning give their reasons. The fourteen that were refused only as *not in this ADR's
   first batch* — a schedule, not a reason — were each read on doc.esri.com and decided (`_lastools.py`,
   `test_lastools.py`, twenty tests on LAS files the tests write): **ten built** — ConvertLas, ProjectLAS, ColorizeLas,
   ThinLas, TileLas, ClassifyLasOverlap, LocateLasPointsByProximity, LocateOutliers, InterpolateFromPointCloud and
   ExtractObjectsFromPointCloud (its multipatch types refused); **one equivalent** — ClassifyLasBuilding, whose page
   names a "plane detection process" without describing it, built as region growing with a smoothness constraint
   (Rabbani et al. 2006); **three refused for their own reason** — ExtractPowerLinesFromPointCloud (a catenary fit
   whose grouping, fitting and wind correction are not described), ExtractRailsFromPointCloud (it needs Esri's deep
   learning frameworks and does not describe the detection) and ExtractMeshFeaturesUsingPointCloud (an integrated
   mesh in, multipatch out). Their INFERRED choices are §5.9 (i)–(r). No name in grapy is refused as a batch any more,
   and `test_lastools.py` fails if one is.
5. **LAZ is read in the image**: a LAZ backend (lazrs, MIT/Apache-2.0, or laszip) is chosen, its licence recorded, and
   a LAZ file read end to end — or LAZ is decided out.
6. **CONSTRAINED_DELAUNAY** is built with a permissively licensed triangulator, or refused for good with the search
   recorded.
7. **The owner confirms or changes §5.9** ([Q-196](../open-questions.md)).
