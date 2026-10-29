# ADR-224 — grapy aims at arcpy's whole buildable surface

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-29, by owner decision. Asked for *"grapy'da arcpy da olan tüm yetenekleri istiyorum"*, then shown the measured size and three groups, the owner chose: scope *"Önce 1. grup, sonra 2."*; order *"İkisi paralel"* (vector and raster together); releases *"Her partiyi yayımla"*. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md) §5.2 made grapy keep arcpy's documented names one to one and
build what it builds on shapely, pyproj, pyogrio and pyarrow. It did not say how much. Condition 6 counted it: 90 of
the 896 public names in eight modules (`tools/grapy_surface.py`). The owner then asked for everything arcpy has.

**Measured with `dir()` on the arcpy in ArcGIS Pro 3.7 (names only):** arcpy has 63 submodules. Its flat
`toolbox` module holds 1,759 tool functions. `cim` holds 1,467 classes, and `mp` holds the map and layout object model.
The modules split into three groups by what they need:

1. **Buildable on this server's own data.** These are: arcpy's own functions and classes, `da`, `management`,
   `analysis`, `conversion`, `cartography`, `edit` and `stats`; the raster modules `sa`, `ia` and `ddd`; and `ga`.
   They are about 1,800 names.
2. **Buildable after a data model.** These modules work on an Esri data model that this server does not hold:
   `un` (utility network), `parcel`, `nax`/`nd`/`na` (network datasets), `topographic`, `locref`, and geodatabase
   versioning, replicas and topology inside `management`. Each model has to exist here before its tools can.
3. **Not buildable as documented.** These modules answer from Esri's data or cloud services: `ba` (Business
   Analyst's demographics), the world geocoder behind much of `geocoding`, `agolservices`, and `geoai`'s hosted
   models.

## 2. Alternatives considered

### Alternative A — grow grapy on demand

Build a tool when a toolbox needs it. **For:** no work is spent on tools nobody calls. **Against:** a toolbox written
against arcpy fails on the first missing name. The owner asked for the whole surface, and *on demand* never closes.

### Alternative B — all three groups

**For:** it is the literal ask. **Against:** group 3 cannot be the documented behaviour. A demographic answer without
Esri's demographics is a different answer under the same name, which §5.2's *one to one* forbids.

### Alternative C — group 1, then group 2 (chosen)

**For:** every name that can behave as documented is built. A data model is built when its tools are built.
**Against:** months of work, and group 2 is several ADRs in itself.

## 3. Counterarguments to the preferred option

Arcpy is enormous, and much of it exists for desktop workflows: layouts, map documents, licensing. A server
running user tools may never need them. Counting all of it makes the gauge look worse than the product is.
The answer is that the counter shows each module separately, so a reader can see which modules are behind.

## 4. Evidence

`tools/arcpy-surface.json` holds the eight-module list. The 63-module size above was measured on 2026-10-29 in
this session; condition 1 below moves it into the repository.

## 5. Decision

1. **Scope is group 1, then group 2.** Group 3's names are refused with NotBuiltError naming them and the reason
   (*"needs Esri's data"*). Where this server has an equivalent, the message names it.
2. **Vector and raster go in parallel.** Each batch carries some of each.
3. **Each batch is released** when CI is green (owner's *"Her partiyi yayımla"*), and the counter on the status page
   moves with it. This replaces the 2026-10-14 rule that work waits on a branch for the owner's word, but only for
   grapy batches.
4. **Every tool is tested against a hand-worked answer**, never against its own output read back. A parameter it does
   not honour is refused by name, never ignored.
5. **Raster needs one step in the server first:** an image service must reach a tool as a file, as a hosted layer does
   (ADR-223 §5.5). Until it does, `sa` and `ia` run on rasters the tool makes or reads from its own run directory.

## 6. Consequences

- **State.** None in the catalogue or at runtime: grapy is a library in the image, and a run's files live in its own directory for the run.
- The image grows by the raster wheels: rasterio or GDAL's Python bindings, and scipy.
  ADR-016 §7 and the curated wheel set grow with them.
- The surface counter widens from eight modules to every group-1 module. Its total changes, and the status page says why.

## 7. Assumptions this decision rests on

- The open libraries can reproduce documented arcpy behaviour for group 1. Some tools document an algorithm only by
  name (for example `sa.Kriging` options). Where documentation does not define the answer, the choice is labelled
  INFERRED in the tool's docstring and listed for the owner.

## 8. Dependencies

[ADR-223](ADR-223-user-tools-run-sandboxed-on-grapy.md), [ADR-016](ADR-016-packaging-deployment-upgrade.md) §7, clean room
(CLAUDE.md §5): only documented behaviour is reproduced; Esri's implementation is never read.

## 9. Revisit triggers

- A group-3 service gains an open equivalent with the same documented answer.
- The image size or build time from raster wheels becomes a deployment problem.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The counter covers every group-1 module**, from a list read from arcpy and kept in the repository. **DISCHARGED 2026-10-29** (26a715f5): `tools/arcpy-surface.json` adds `sa`, `ia`, `ddd` and `ga`, read from Pro 3.7; 1,819 names.
2. **Image services cross into a tool as files** (§5.5), and `sa` reads one end to end. **DISCHARGED 2026-10-29**: a raster parameter (`GPRasterLayer`, `DERasterDataset` and the like, served as `GPRasterDataLayer`) takes an image service of this server the caller may read. The runner writes its first band as raw doubles beside a header, and grapy turns that into the GeoTIFF the tool opens. `An_image_service_reaches_a_tool_as_a_raster_it_reads_cell_for_cell` uploads a corpus image and reads four cells, NoData included, against the values `truth.json` records. A mosaic of several images, and an image of more than 25 million cells, are refused by name.
3. **Group 3 is refused with its reason**, tested. **DISCHARGED 2026-10-29**: `arcpy.ba`, `agolservices`, `geoai` and `geocoding` answer every tool with NotBuiltError naming the tool and saying it answers from Esri's data or services. Group 2's modules (`un`, `parcel`, `na`, `nax`, `nd`, `topographic`, `locref`) say they need a data model this server does not hold. Group-3 tools inside a group-1 module (`analysis.Enrich`, `EnrichLayer`) are refused the same way. Tested in `test_a_module_on_esris_data_or_on_a_model_not_held_is_refused_with_its_reason`.
4. **Group 1 is complete**: the counter reads every group-1 name built.
5. **Group 2 has an ADR per data model** before its tools are built.
