# ADR-224 — grapy aims at arcpy's whole buildable surface

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-29, by owner decision. Asked for *"grapy'da arcpy da olan tüm yetenekleri istiyorum"*, then shown the measured size and three groups, the owner chose: scope *"Önce 1. grup, sonra 2."*; order *"İkisi paralel"* (vector and raster together); releases *"Her partiyi yayımla"*. **Amended 2026-10-30** by two more answers: undocumented tools are built as equivalents under arcpy's name (*"Aynı adla, yakın bir eşdeğer"*, §5.6), and group 2 begins with topology and relationship classes, network datasets, versioning and archiving (§5.7). |
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
   (*"needs Esri's data"*). Where this server has an equivalent, the message names it. **Narrowed 2026-10-31 by
   [ADR-248](ADR-248-grapy-carries-deep-learning-on-pytorch.md)** (owner decision, *"Tam aç"*): the deep learning names
   once refused here for want of a framework are built on PyTorch, except the two whose models are Esri's or whose page
   could not be read. The point cloud deep learning names in `arcpy.ddd` are built on the same foundation by
   [ADR-249](ADR-249-grapy-carries-deep-learning-on-point-clouds.md).
2. **Vector and raster go in parallel.** Each batch carries some of each.
3. **Each batch is released** when CI is green (owner's *"Her partiyi yayımla"*), and the counter on the status page
   moves with it. This replaces the 2026-10-14 rule that work waits on a branch for the owner's word, but only for
   grapy batches.
4. **Every tool is tested against a hand-worked answer**, never against its own output read back. A parameter it does
   not honour is refused by name, never ignored.
5. **Raster needs one step in the server first:** an image service must reach a tool as a file, as a hosted layer does
   (ADR-223 §5.5). Until it does, `sa` and `ia` run on rasters the tool makes or reads from its own run directory.
6. **Where Esri's documentation does not define the answer, the tool is built anyway, under arcpy's name, as the
   standard method of the literature** — owner decision 2026-10-30, *"Aynı adla, yakın bir eşdeğer"*, asked with the
   alternatives of keeping it refused or naming it differently. On 2026-10-29 every one of the 1,819 group-one names had
   been decided: 615 built and 1,204 refused, of which about 300 were refused because the documentation leaves the
   algorithm or the output undefined (Kernel Density's formulas only as images, Kriging's fitting, FlowDirection's flats,
   SmoothLine's PAEK, Stretch …). **Such a tool is an *equivalent*, not a reproduction**, and it says so three ways:
   - its docstring begins `EQUIVALENT:` and names the method and its source (a paper, a textbook, a public standard);
   - it is marked with `_runtime.equivalent(...)`, which `tools/grapy_refusals.py` reads, so the status page counts
     equivalents apart from the tools built to their documentation;
   - its run says so in the tool's messages, once per run: a user of the toolbox sees that the number may differ from
     ArcGIS Pro's.
   The rules of §5.4 still hold within it: what the equivalent does not do is refused by name, and it is tested against
   a hand-worked answer of the method it names.
7. **Group 2 begins with three data models**, owner decision 2026-10-30, in this order of ADRs, each before its tools:
   topology and relationship classes, network datasets, versioning and archiving. They are grapy's data models, held in
   the GeoPackage a tool works on.

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
4. **Group 1 is complete**: the counter reads every group-1 name built. **PARTLY DISCHARGED 2026-10-29**: every name is *decided* — `tools/grapy_refusals.py` read 615 built, 1,204 refused with a reason and none unexamined. §5.6 turns the undocumented refusals into equivalents; the rest of the refusals are groups 2 and 3, a Pro application, no network from a fenced tool, or grapy's own limits (Z on features, raster attribute tables, multiband rasters), which are this project's to remove.
   **Read again 2026-10-31, still PARTLY DISCHARGED.** `tools/grapy_refusals.py` on branch grapy-a: the twelve group-1
   modules hold 1,819 names — 1,260 built (197 of them equivalents) and 559 refused, none unexamined. Every name is
   decided and a refusal names its own reason, but the condition asks for every name *built*, and that is not true,
   nor can all of it become true: a keyword sort of the 559 reasons finds about 150 whose page leaves the answer or
   the format undefined (or answered 404), about 80 that are ArcGIS Pro's application (layouts, symbology, a map
   view), about 50 that answer from Esri's data or services, about 40 enterprise-geodatabase administration, about
   15 unpublished Esri formats, about 15 a network a fenced tool does not have, and about 200 of reasons of their
   own (installation and licence queries, DBMS users and roles, colormapped or pansharpened rasters grapy does not
   keep, and so on). Two refusals still carry a schedule the guard test's words miss — `da.TableToArrowTable` and
   `da.CreateParquetCache` say pyarrow "is not among the wheels a user tool runs with today", and the image installs
   pyarrow. *(Repaired 2026-10-31: `da.TableToArrowTable` is built to its page and to* Apache Arrow in ArcGIS *on
   pyarrow, which CI's grapy jobs now install as the image does; `conversion.ExportToParquet`, refused for the same
   missing wheel, is built as a GeoParquet 1.1 equivalent; `da.CreateParquetCache` is refused because its page
   defines neither the cache's form nor where it is kept, and no grapy reader opens a Parquet file. The guard test
   `test_lastools.Refused.test_no_refusal_anywhere_reads_as_a_schedule` now reads every refused name's answer for
   *yet*, *today*, *until*, *later* and their kind.)* What is missing for the condition as written is a decision rather than work: either the owner narrows
   it to *every group-1 name built or refused with its own reason* — which holds today — or the refusals above are
   reopened one by one.
5. **Group 2 has an ADR per data model** before its tools are built. **PARTLY DISCHARGED 2026-10-31**: §1's group 2
   names `un`, `parcel`, the network modules, `topographic`, `locref`, and versioning, replicas and topology inside
   `management`. Most have their ADR and were built after it — topology ([ADR-225](ADR-225-grapy-holds-topology-and-relationship-classes.md)),
   network datasets for `na`, `nax` and `nd` ([ADR-226](ADR-226-grapy-holds-network-datasets.md)), versioning, archives
   and replicas ([ADR-227](ADR-227-grapy-holds-versions-and-archives.md), [ADR-235](ADR-235-grapy-holds-attribute-rules-contingent-values-and-replicas.md)),
   the utility network `un` ([ADR-231](ADR-231-grapy-holds-utility-networks.md)) — and so were the models §5.7's
   order added (subtypes, LAS and TIN, mosaic, network diagrams, geostatistical layers, terrain, multidimensional,
   SAR, multipatch: ADR-228 to ADR-238). Three have none: the parcel fabric (`arcpy.parcel`), the topographic
   production model (`arcpy.topographic`) and linear referencing networks (`arcpy.locref`). No tool of theirs is
   built, so the order the condition asks for is kept; but each module is still refused whole with
   *"needs a data model this server does not hold yet (ADR-224 group 2)"* (`grapy/__init__.py`, `_DATA_MODEL`), a
   schedule of the kind the guard test forbids for names, and none of the three is in `tools/arcpy-surface.json`, so
   the counter does not show them. Missing: an ADR for each (with an assigned number), then their names read into
   the surface file and built or refused one by one. Inside group-1 modules, `arcpy.Dimension`, the BIM file
   workspace tools and the scene layer tools are refused with the same "(ADR-224 group 2)" reason.
6. **Equivalents are counted apart** (§5.6): the status page shows built-to-documentation and built-as-equivalent
   separately, and every equivalent names its method and source and says so in its run. **DISCHARGED 2026-10-31**:
   `tools/grapy_refusals.py` reads `__grapy_equivalent__` and the status page's grapy table has its own column for
   equivalents beside built; `_runtime.equivalent(method, source)` takes both, writes them into the mark and warns once
   per run that the tool is an equivalent, naming them. One of §5.6's three marks did not hold: 18 marked tools'
   docstrings (the solar radiation tools, Classify and ClassifyRaster, the distance accumulation tools,
   `CreateRandomValueGenerator` …) did not begin `EQUIVALENT:`. The decorator now begins any docstring that does not
   with `EQUIVALENT: <method> (<source>).`, and `ia.MLClassify`, marked by hand, says it itself.
   `test_equivalents.py` fails if a built name is marked without a method or source, marked without its docstring
   saying so, or says so without being marked (236 marked names), and checks that a run warns once naming the
   method.
