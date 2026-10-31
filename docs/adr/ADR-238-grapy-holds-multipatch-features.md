# ADR-238 — grapy holds multipatch features

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the measurements, each exact on planar faces (areas, volumes by the divergence theorem, the prism of a regular polygon, the sweep of a box) and checked by hand · `MEDIUM` for the model as a carrier of what ArcGIS's multipatch holds — geometry yes, textures and colours no · `LOW` for agreement with ArcGIS Pro's faces wherever a page names a shape and not its facets (Buffer3D, the shadow volume), §5.2 |
| **Decided** | 2026-10-31, by owner decision: grapy gets multipatch features, and the names refused as *"a data model this server does not hold yet"* are built or refused for a reason of their own ([ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.7). The storage and the rules the pages leave open are INFERRED and listed for confirmation in §5.2 and [Q-206](../open-questions.md). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Before this decision 24 names were refused with a schedule — *"needs a data model this server does not hold yet
(ADR-224 group 2)"*: `arcpy.Multipatch`, 21 tools in `arcpy.ddd` that read or write multipatch features or meshes,
`SetLasClassCodesUsingRaster` (a LAS tool, buildable since ADR-229), and `arcpy.ga.IDW3D` and `NearestNeighbor3D`,
which write a netCDF grid of voxels. A schedule is not a reason (ADR-224 §5.6, `test_no_name_is_refused_as_a_batch`).

What ArcGIS publishes, read on doc.esri.com on 2026-10-31 by agents that quoted the pages, with no Esri implementation
read (CLAUDE.md §5):

- **arcpy documents no `Multipatch` class.** The alphabetical class list holds Annotation, Geometry, Multipoint, Point,
  PointGeometry, Polygon and Polyline, and the Geometry page's constructor and `type` name point, polyline, polygon and
  multipoint only.
- **The multipatch itself is described in public prose**: Pro's .NET SDK `PatchType` enumeration (TriangleStrip,
  TriangleFan, Triangles, FirstRing, Ring) and Esri's white paper *The Multipatch Geometry Type* (2008), which gives the
  strip and fan triangle orders, ring roles (outer, inner, first, ring) and their grouping rules. No storage format is
  published, and GeoPackage has no multipatch type.
- **Most tool pages name a result and not its method.** Only InterpolatePolyToPatch and LasBuildingMultipatch describe
  their steps; Intersect3DLineWithMultiPatch, MultiPatchFootprint, SunShadowVolume and Inside3D name their fields; the
  solid booleans (Intersect3D, Union3D, Difference3D) say what they keep, not how.
- **I3S is a published standard** (OGC Community Standard 17-014r9, 1.3), and its integrated mesh's uncompressed
  geometry buffer is required, Draco optional. The two mesh tools' pages do not say which level of detail they read.
- **The 3D file formats** Import3DFiles names: 3D Studio (.3ds, never published by Autodesk), VRML97 and GeoVRML
  (ISO/IEC 14772-1, GeoVRML 1.1, public), OpenFlight (.flt, OGC 19-065, published), COLLADA (.dae, Khronos, ISO/PAS
  17506, public) and Wavefront OBJ (public).

## 2. Alternatives considered

### Alternative A — faces as a GeoPackage MultiPolygon Z, solids through manifold3d (chosen)

A multipatch feature class is a feature table whose geometry is `MULTIPOLYGON` with z mandatory, each polygon one planar
face, marked by a `grapy_multipatch` row in `gpkg_extensions`. Booleans on closed solids use manifold3d (Apache-2.0), a
robust mesh-boolean library with wheels for every platform the image and CI run on. **For:** every GeoPackage reader
opens the faces as 3D polygons; shapely reads them; the closed-solid booleans are exact-topology and need no code of
ours. **Against:** a reader that does not know the extension sees polygons, not a multipatch; a multipatch's textures
and colours are not held.

### Alternative B — `POLYHEDRALSURFACE Z` or `TIN Z`

OGC Simple Features 1.2's own surface types, which GDAL reads. **For:** the right type name. **Against:** GeoPackage
admits them only through its non-linear geometry types extension, shapely and GEOS do not read them, so every tool would
parse WKB of its own; and a polyhedral surface is one connected surface, where a multipatch may be several. Rejected.

### Alternative C — build the specifiable subset and refuse the booleans

IsClosed3D, the footprint, the extrusions, TINToMultipatch and the bounding volume are within reach without a mesh
library. **Against:** Intersect3D, Union3D, Difference3D, Inside3D on solids and Buffer3D's unions would stay refused,
and a robust boolean of our own is months, not a batch. manifold3d's licence and wheels removed the reason to choose it.

### Alternative D — trimesh beside manifold3d

**For:** a mesh toolkit with I/O. **Against:** grapy needs only booleans and hulls, which manifold3d answers directly;
trimesh would be a second dependency for nothing used.

## 3. Counterarguments to the preferred option

**A multipatch made by ArcGIS cannot be opened, and grapy's cannot be opened by Pro as a multipatch.** True: Esri's
storage of a multipatch in a file geodatabase is not published (clean room). A toolbox that brings one must import it
— Import3DFiles reads OBJ, COLLADA and VRML.

**manifold3d's results differ from Pro's in their triangles.** They will: neither the triangles nor their count is
documented by any of the boolean pages. The tests check what is documented and computable — volumes, closure, which
features are kept or removed.

**Pro's ring orientation is the opposite.** Esri's convention is clockwise seen from outside; grapy writes OGC's
(counterclockwise, the right-hand normal outwards), because the geometry is an OGC type and every OGC reader expects it.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Closure, areas and volumes are exact on boxes and planes | `test_multipatch.py`: 52 and 24 for a 2×3×4 box, 1,500 under the plane z = x + y to −5, 25√3 draped, 375 between two TINs | hand integration |
| A straight 3D buffer is a prism over a regular polygon | volume 10 · 10 sin(2π/20) for r = 1, L = 10, 20 segments | geometry |
| A box's shadow volume is its sweep along the rays | abc + L(bc\|dx\| + ac\|dy\| + ab\|dz\|) to 1e-6 relative | the Minkowski sum of a box and a segment |
| A box's shadow on flat ground | every one of 3,600 cells against the convex hull of its footprint and footprint moved by h / tan(elevation) | geometry |
| The sun's position | declination −7.78507° and equation of time 13 m 42.6 s on 1992 Oct 13.0 | Meeus 1998, examples 25.a and 28.b |
| IDW in 3D | 7.0 at (5, 0, 0) from 0, 10, 20, 30 at distances 5, 5, 15, 15, power 2 | hand arithmetic |
| manifold3d installs on CI's platforms | wheels cp39–cp314 for manylinux_2_28 x86_64 and aarch64, Windows and macOS; the image is Ubuntu noble (glibc 2.39) | PyPI, 2026-10-31 |

No performance claim is made: a user tool runs under ADR-223's limits, and a mesh too large fails by those limits.

## 5. Decision

1. **grapy holds multipatch features**, and builds 18 of the 22 `arcpy.ddd` names and both `arcpy.ga` names on them:
   IsClosed3D, MultiPatchFootprint, MinimumBoundingVolume, TINToMultipatch, InterpolatePolyToPatch, ExtrudeBetween,
   Intersect3DLineWithMultiPatch, Inside3D, Intersect3D, Union3D, Difference3D, Buffer3D, FenceDiagram,
   LasBuildingMultipatch, SetLasClassCodesUsingRaster, SunShadowFrequency, Import3DFiles, SunShadowVolume (an
   **equivalent**, ADR-224 §5.6: Crow 1977's swept shadow volume), IDW3D and NearestNeighbor3D. AddZInformation's
   SURFACE_AREA and VOLUME, refused for the same missing model, are built with them.
2. ~~**INFERRED, for confirmation**~~ **Confirmed 2026-10-31 by owner decision in Q-206** (*"Hepsini onayla"*) ([Q-206](../open-questions.md)):
   - (a) **storage**: a MultiPolygon Z feature table marked `grapy_multipatch` in `gpkg_extensions`, one polygon per
     planar face; Esri's patches stored as the faces they describe (a strip or fan as its triangles, a ring group as a
     polygon with holes); faces counterclockwise seen from outside; textures and colours not held; `SHAPE@` reads the
     faces as a Polygon geometry;
   - (b) **closure**: every edge of the welded (10⁻⁶), triangulated faces used exactly twice, once each way;
     IsClosed3D's field `IsClosed` SHORT 1/0;
   - (c) **faceting**: Buffer3D's circle of `buffer_quality` sides and sphere of as many meridians and half as many
     bands; STRAIGHT joints as the hull of the two cylinders' end circles with flat ends, ROUND as spheres at every
     vertex; MinimumBoundingVolume's default CONVEX_HULL and points defaulting to ALL;
   - (d) **fields the pages do not name**: Inside3D's Status `Inside`/`Partially_Inside`; Union3D's table `Output_ID`,
     `Input_ID`; Difference3D's −1 for an untouched feature; Intersect3D's second input's fields with `_1`;
     Import3DFiles' `Name`; InterpolatePolyToPatch's `Area`/`SArea` defaults; IDW3D's cross-validation `Measured`,
     `Predicted`, `Error`; the inputs' fields kept by Buffer3D, ExtrudeBetween, FenceDiagram and LasBuildingMultipatch;
   - (e) **the open rules**: ExtrudeBetween's roof the higher TIN on average; FenceDiagram's profiles ordered by mean
     height, one feature per line; LasBuildingMultipatch's footprint vertices at the nearest point's height, bins from
     the footprint's corner, withheld points never used; SetLasClassCodesUsingRaster's cell owning its upper-left edges;
   - (f) **the sun**: NOAA's algorithm without refraction; Windows zone names at their standard offsets, DST as one
     hour ahead throughout; SunShadowVolume's extrusion until the highest point's shadow reaches the lowest point's
     height, its default zone the system's, `HUGS_FEATR` never added; SunShadowFrequency's default zone UTC, without an
     interval the start time each day;
   - (g) **voxels**: netCDF-3 with dimensions `elevation`, `y`, `x`, a variable per field, NaN or −1 as fill, the WKT
     as `crs_wkt`; NearestNeighbor3D's ties to the first category; IDW3D's inflation factor searched over 61 log-spaced
     values then Brent's method;
   - (h) **Import3DFiles**: a non-planar face as a fan of triangles; COLLADA's `up_axis` and `unit` honoured and
     `y_is_up` applied to the other formats; GeoVRML's GD as EPSG:4979 (WGS 84 only), UTM as 326xx/327xx, GC as 4978;
     Inline, GeoOrigin, GeoLocation, GeoElevationGrid and GeoLOD refused by name.
3. **Refused for their own reasons**: EncloseMultiPatch (its grid drape is not specified), ExtractLOD2Buildings (its
   roof segmentation and generalisation are not given), ExtractMultipatchFromMesh and ExtractMeshFeaturesUsingPointCloud
   (the level of detail read, and the subdivision, are not given), `arcpy.Multipatch` (not documented). Within built
   tools, by name: MinimumBoundingVolume's SPHERE and CONCAVE_HULL, Intersect3D's SURFACE and LINE, the z-tolerance
   simplification of TINToMultipatch and LasBuildingMultipatch, natural neighbours on a TIN, IDW3D's sectors and
   data-derived defaults (as ADR-233 refuses them in 2D), 3D Studio files (unpublished) and OpenFlight files (its
   specification could not be obtained on 2026-10-31).
4. **One new wheel**, manifold3d (Apache-2.0), in the image, both CI jobs and DEPENDENCY-LICENSES.md.

## 6. Consequences

**State.** None in the catalogue or the server: multipatch features live in the GeoPackage a tool works on, and the
netCDF and 3D files in its run directory.

**Positive.** A user tool can build, measure and combine 3D buildings, shadows and lidar roofs on this server; 20 names
move from refused to built; no name in grapy says *not held yet* for a multipatch.

**Negative.** The image grows by one compiled wheel (about 2 MB). A multipatch's appearance is lost on import. Several
refusals elsewhere said multipatch features were not held ([D-329](../architecture-debt.md), closed by §12).

**Ports created.** manifold3d is reached only through `_multipatch.Solid` and `_mptools.buffer_solid`; no manifold3d
type leaves those.

**Names.** `tools/grapy-refusals.json` before → after: `arcpy.ddd` built 85 → 103 (equivalents 7 → 8), refused 71 → 53;
`arcpy.ga` built 23 → 25, refused 19 → 17; `arcpy` built 101, refused 82 (the reason of `Multipatch` changed); none
unexamined.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A user tool's meshes fit in its run's memory; manifold3d works on them whole | not measured |
| — | Faces written by other tools are planar, as a multipatch's faces are; a face that is not is triangulated on its best plane | stated |

## 8. Dependencies

**Depends on:** [ADR-224](ADR-224-grapy-aims-at-arcpys-whole-buildable-surface.md) §5.6–5.7,
[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) (the fence and limits),
[ADR-229](ADR-229-grapy-holds-las-datasets-and-tins.md) (TINs and LAS datasets),
[ADR-233](ADR-233-grapy-holds-geostatistical-layers.md) (search neighbourhoods), clean room (CLAUDE.md §5).

**Depended on by:** the tools still refusing multipatch features in `conversion`, `management` and `ddd` (D-329).

## 9. Revisit triggers

- OGC's OpenFlight 16.0 document is obtained: `.flt` becomes buildable.
- A page names EncloseMultiPatch's grid method, or the mesh tools' level of detail.
- A user needs textures: the model would hold them beside the faces.

## 10. Dissent

None recorded.

## 11. Conditions

1. **Every built tool is tested against a hand-worked answer.** **DISCHARGED 2026-10-31**: `test_multipatch.py`, 28
   tests (§4's table).
2. **The owner confirms or changes §5.2** ([Q-206](../open-questions.md)). **DISCHARGED 2026-10-31** — confirmed as built by owner decision in Q-206 (*"Hepsini onayla"*).
3. **The other names that refuse multipatch features as not held** — `conversion.MultipatchToCollada` and
   `MultipatchToRaster`, `management`'s 3D-object and level-of-detail tools, `ddd.SkylineBarrier`,
   PointFileInformation's EXTRUSION, ExtractObjectsFromPointCloud's 3D types and LocateLasPointsByProximity's multipatch
   features — are built on this model or refused for a reason of their own ([D-329](../architecture-debt.md)).
   **DISCHARGED 2026-10-31** by D-329's sweep (§12): MultipatchToCollada, MultipatchToRaster, SkylineBarrier,
   Export3DObjects, Import3DObjects, EXTRUSION, the 3D convex and concave hulls and the bounding sphere, and multipatch
   proximity are built; Add3DFormats, Remove3DFormats, GenerateLevelOfDetail, BOUNDING_BOX_3D and silhouettes are
   refused, each for its own reason; `test_lookagain.py`. *(Looked at again 2026-10-31: no name refuses multipatch or 3D object features as not held. One
   neighbour still gave a schedule — `conversion.MeshToLAS`, "an integrated mesh … which grapy does not hold (ADR-224
   group 2)", listed beside these in ADR-234 §5.8 — and now refuses for its own: its page names how points are made but
   not which level of detail is read, nor where a face's one sample lies.)*

## 12. Amendment 2026-10-31 — D-329's sweep: the names that still said a model was missing

Four models arrived on one day — multidimensional rasters (ADR-236), SAR data (ADR-237), multipatch features (this
ADR) and editing behaviours with COGO (ADR-240) — and refusals written before them still gave their absence as the
reason. Each name was looked at again, its page read on doc.esri.com on 2026-10-31 (curl and quoted; no Esri
implementation read, CLAUDE.md §5), and built or refused for a reason that is true now. No new ADR: every name is built
on a model these four ADRs already hold, and the formats written are published standards.

1. **Built on multipatch features** (`grapy/_mpmore.py`): `conversion.MultipatchToCollada` (COLLADA 1.4.1 and 1.5.0,
   Khronos/ISO PAS 17506; a KML 2.2 placemark beside it when the class is projected, as the page says),
   `conversion.MultipatchToRaster` (a vertical line from each cell centre, vertical faces left out, the cell size
   rules of the page), `ddd.SkylineBarrier` (lines; the fan, CLOSED and PROJECT_TO_PLANE), `management.Export3DObjects`
   and `Import3DObjects` (COLLADA, Wavefront OBJ, glTF 2.0 — ISO/IEC 12113:2022 — as .gltf and .glb),
   PointFileInformation's EXTRUSION, ExtractObjectsFromPointCloud's CONVEX_HULL_3D (the default), CONCAVE_HULL_3D
   (the 3D alpha shape, Edelsbrunner and Mücke 1994) and BOUNDING_SPHERE (Welzl 1991, in Gärtner's move-to-front form),
   LocateLasPointsByProximity on multipatch features (the distance to the nearest face, Ericson 2005 §5.1.5),
   `da.CreateTable` and `management.RegisterWithGeodatabase` with MULTIPATCH.
2. **Built on multidimensional rasters** (`grapy/_mdmore.py`): `ia`/`sa.Subset`, `ia`/`sa.Foreach`,
   `ia.SummarizeCategoricalRaster`, `management.ExtractSubDataset` (HDF and NITF subdatasets through GDAL),
   `arcpy.NetCDFFileProperties` (typed, through scipy's netCDF-3 reader; a netCDF-4 file refused by value), CopyRaster's
   ALL_SLICES. **On SAR data**: `ia`/`sa.Complex`, the magnitude of complex cells. **On COGO lines**
   (`grapy/_cogo.py`): `edit.UpdateCOGO`. Beside them, `da.CreateTable`'s z and m and `NumPyArrayToFeatureClass`'s z and
   m fields, refused as *"grapy's shapes hold neither"*, which they do.
3. **One netCDF writer.** `_mdim.netcdf3` writes every netCDF grapy makes: ADR-236's multidimensional rasters and this
   ADR's voxels (§5.2 (g)) both go through it. The two layouts stay different — a voxel grid ascending in y, its
   categories integers with −1 as fill — and the writer does not care; the voxel's `elevation` now carries CF's
   `axis` and `positive`, so `Raster(path, True)` reads it as `StdZ`.
4. **Refused, each for its own reason**: Add3DFormats and Remove3DFormats (the 3D object feature class's format tables
   are not named or laid out by either page), GenerateLevelOfDetail (it works on integrated mesh scene layer packages,
   not multipatch, and does not say how coarse levels are simplified), CreateVoxelSceneLayerContent (a voxel layer is a
   local scene's display layer), BuildMultidimensionalTranspose (CRF only, transpose layout unpublished),
   BuildMultidimensionalInfo (the Dimensions field's content is not given), ExtractWater (its classifier is not named);
   by value or parameter: SkylineBarrier's silhouettes, ExtractObjectsFromPointCloud's BOUNDING_BOX_3D (exact and
   approximate oriented boxes differ and the page names none), the formats DWG and FBX (Autodesk's, unpublished), IFC
   (implicit geometry whose tessellation is not specified), USDC and USDZ (the crate binary has no published
   specification) and CityGML (module and level of detail not given), CopyRaster's TRANSPOSE. The six colour and
   pansharpening names kept their reason, less its *multidimensional* clause.
5. **Fixed in passing**: ExtractObjectsFromPointCloud's BOUNDING_BOX_2D is the smallest *oriented* rectangle, as its
   page says; it was the axis-aligned box.
6. ~~**INFERRED, for the owner**~~ **Confirmed 2026-10-31 by owner decision in Q-209** (*"Hepsini onayla"*) ([Q-209](../open-questions.md)): (a) Subset's dimension names and intervals; (b)
   Foreach's slice arguments; (c) SummarizeCategoricalRaster's table; (d) ExtractSubDataset's index from 1; (e)
   NetCDFFileProperties' field type words and dimension values; (f) a 3D object feature class is a multipatch class,
   and the models' origin, naming and axes; (g) MultipatchToRaster's grid corner; (h) SkylineBarrier's pairing,
   ORIGFTR_ID, radii and planes; (i) UpdateCOGO's direction, corrections and arcs; (j) a flat extrusion; (k) the LAS
   tools' 3D shapes and distances; (l) Complex on a real raster.

**Names, before → after** (`tools/grapy-refusals.json`): built 1,256 → 1,271, refused 918 → 903, equivalents
unchanged, none unexamined.
