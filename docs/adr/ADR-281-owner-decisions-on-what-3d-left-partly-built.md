# ADR-281 — The owner's four answers on what 3D left partly built

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-07, **by owner decision**, four numbered questions with a recommendation each, answered the same evening (§5). How each is carried out is this ADR's. |
| **Supersedes** | [ADR-273](ADR-273-the-maps-sdk-is-4-34.md)'s choice of 4.34 (its revisit trigger, *a need only 5.x meets*, fired: b3dm and Gaussian splat). It also amends v1-scope's Scenes row for the three raster functions in §5.4. |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

After the fourth night, the ArcGIS comparison ([3d-parity-arcgis.md](../reviews/3d-parity-arcgis.md)) had no *Owed* row.
Five rows were *Partly built*, each waiting on the owner:

- the web style gallery;
- raster analysis from the scene;
- 3D Tiles 1.0 (b3dm) and Gaussian splat, which SDK 4.34 does not draw;
- mesh editing;
- an empty mesh layer.

## 2. Alternatives considered

The four questions, each with its options as asked. **(R)** marks the recommendation.

1. **Mesh editing, first step** ([ADR-280](ADR-280-3d-object-layers-hold-meshes.md)). The options were:
   - glb only (R);
   - glb plus a server-side converter;
   - not now.

   **Answer: glb + converter.**
2. **Maps SDK 5.** The options were:
   - measure first (R);
   - switch now;
   - not now.

   **Answer: switch now.**
3. **Web style gallery.** The options were:
   - our own small gallery (R);
   - Esri's public gallery;
   - not now.

   **Answer: our own small gallery.**
4. **Raster analysis from the scene.** The options were:
   - a separate scope (R);
   - a few tools now (slope, aspect, hillshade over an elevation surface);
   - not now.

   **Answer: a few tools now.**

## 3. Counterarguments to the preferred option

- **Switching the SDK without measuring first** puts every map page on a new major version in one change. That is the
  risk the recommendation was for. It is answered by running every console test that loads the SDK against it before
  the change is kept (§4). A deployment can pin 4.34 by its address, and still be served.
- **A converter** is a Tier 2 library that reads formats with long histories of parser faults (FBX, IFC, DWG). It is
  its own ADR, with a licence check and a sandbox question, under ADR-280.

## 4. Evidence

All on the fixture server, 2026-11-07, with SDK 5.2.0.

- **The SDK.**
  - Every console test class that loads the SDK passed on 5.2: **95 of 95** (`AnalyzeDialogTests`, the `Scene*`,
    `WebMap*` and `TilesArchive` classes, `ViewerPageTests`, `UiSweepOf20261102Tests`, `TheServersGroundIsDrawnTests`).
  - Loading `core.js` as a module was measured first on a bare page: it defines `require` and `$arcgis.import`, and
    resolves `esri/…` ids.
- **3D Tiles 1.0.**
  - A b3dm archive of a 50 m box is drawn by 5.2: a layer view is made, the SDK fetches `box.b3dm`, and the box stands
    where its tileset puts it (screenshot, not committed).
  - SDK 4.34 made no layer view for the same archive (ADR-278 condition 2).
- **Gaussian splat, partly.**
  - The scene viewer reads a tileset whose glTF content declares `KHR_gaussian_splatting` and adds it as the SDK's
    `GaussianSplatLayer`.
  - On a bare 5.2 page, Esri's public splat sample gets a layer view in software rendering.
  - **A splat tileset of our own is refused** ("Invalid tileset JSON"). This is an uncompressed `KHR_gaussian_splatting`
    glb, tried five ways: a region or a box; with and without `ESRI_crs` in UTM on the tileset and on the tile; content
    on the root or in a child; an external tileset. What Esri's sample has and ours does not is SPZ compression
    (`KHR_gaussian_splatting_compression_spz_2`).
  - **Bisected the same night.** Esri's tileset JSON, served from this server's origin, is accepted. With the SPZ
    extension taken out of its `3DTILES_content_gltf` lists it is refused exactly as ours was. Taking out `ESRI_crs`,
    `asset.extras` or the 1.1 version changes nothing. **SDK 5.2's splat layer takes SPZ-compressed splats only.**
  - **A real splat archive is drawn from this server.** Esri's sample was cut to its root, one sub-tileset and one
    75 KB SPZ glb, zipped as a `.3tz`, uploaded to the fixture and added in the scene viewer. It was added as a splat
    layer, the SDK fetched the sub-tileset and the glb from `3DTilesServer`, and the splats were drawn. This was kept
    local and deleted: the sample's licence is not stated.
- **The item type.** A public ArcGIS Online 3D tiles item is typed `3DTiles Service`, with no space (item
  `01596d71a5ce40ec8a6f21baad6e5b6f`, read 2026-11-07). ADR-278's inferred `3D Tiles Service` was wrong and is
  corrected.
- **The gallery.** `SceneViewerToolsTests.A_point_layer_is_drawn_as_a_model_from_the_gallery`: twelve models are
  offered, the tree is drawn at its 6.2 m, the form reads it back, and the glb is served as `model/gltf-binary`.
- **Terrain.** `SceneViewerToolsTests.Terrain_drapes_a_surfaces_slope_on_the_scene`: the server's image service is
  offered. Slope adds an imagery layer with the `Slope` raster function, the scene asks the server for it, the server
  answers the function, and the saved scene keeps it.

## 5. Decision

1. **Mesh: glb and a converter.** [ADR-280](ADR-280-3d-object-layers-hold-meshes.md) keeps its prototype as the first
   step. The converter becomes its own ADR when the prototype stands.
2. **SDK 5.2**, the newest release on Esri's CDN on 2026-11-07 (`5.3` answers 404). 5.x has no `init.js`. Each page
   loads `core.js` as a module, which still defines `require` and `$arcgis.import` and resolves `esri/…` module ids, so
   the pages' code is unchanged. An address ending in a 4.x version is loaded the classic way, so a deployment that pins
   4.34 keeps working. `Graticula:MapSdkUrl`'s default is `https://js.arcgis.com/5.2/`.
3. **Our own gallery.** These are glTF models this product makes from boxes, cylinders, cones and spheres
   (`tools/scene-symbols.py`, deterministic, committed with its output). They are served beside the page at
   `/studio/symbols/`, with `gallery.json` naming each one's title, category and height. A point layer is drawn as one
   from its style's *Drawn as*, at the model's height unless the reader sets another. The web scene keeps the model's
   address as an object symbol's `resource.href`, which any client reads. There is no Esri web style format and no
   Esri content.
4. **Terrain.** A tool in *3D analysis* for the scene's editor. It takes one of this server's image services
   (elevation surfaces first) and draws **Hillshade**, **Slope** (degrees or percent rise) or **Aspect**, worked out by
   the server's own raster functions (`RasterFunction.cs`, ADR-136). The result is an imagery layer draped on the scene
   and kept in it with its raster function. Nothing new is stored on the server. **A result saved as a new image
   service is not part of this decision.** It would need a raster write path the server does not have.

## 6. Consequences

- **State.** None new. The gallery is static files. Terrain's layers are in the web scene. The SDK is an address.
- Every page that draws a map runs on a new major version of a third-party library.

## 7. Assumptions this decision rests on

- 5.2's `core.js` keeps the AMD `require` for the life of 5.x. `INFERRED` from 5.2's own file. If a later 5.x drops
  it, the pages move to `$arcgis.import`, which the same file already offers.

## 8. Dependencies

ADR-020, ADR-136, ADR-182, ADR-270, ADR-272, ADR-273, ADR-278, ADR-280.

## 9. Conditions

1. **Every console test that loads the SDK passes on 5.2.** *(Discharged 2026-11-07, 95 of 95, §4.)*
2. **b3dm and Gaussian splat measured on 5.2**, and ADR-278 conditions 2 and 3 answered again. *(Partly discharged
   2026-11-07, §4: b3dm is drawn, and a splat archive (SPZ, as ArcGIS's tools write them) is drawn from this server.)*
   *(Discharged the same night: `GaussianSplatArchiveTests` writes 600 splats of its own as SPZ. The format is Niantic's
   open one (MIT reference). It is written as **version 3**, flagged antialiased, with "smallest three" rotations,
   which is what Esri's sample turned out to hold under an extension named `spz_2`; written as version 2, the splats
   loaded and drew nothing. The archive is uploaded, offered among 3D tiles layers, added as a splat layer, its glb
   fetched from `3DTilesServer`, and the sphere drawn (screenshot, not committed).)*
3. **The gallery**: a point layer drawn as a model and read back. *(Discharged 2026-11-07, §4.)*
4. **Terrain**: a slope added from an image service, asked of the server, and kept in the scene. *(Discharged 2026-11-07, §4.)*
5. **The converter's ADR** (§5.1). *(Discharged 2026-11-07: [ADR-282](ADR-282-3d-models-are-converted-by-assimp-in-its-own-process.md).)*
