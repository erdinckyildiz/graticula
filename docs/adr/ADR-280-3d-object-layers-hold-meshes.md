# ADR-280 — A 3D object feature layer holds meshes, and the scene edits them

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `LOW` |
| **Decided** | 2026-11-07, as a design only. **That 3D editing starts now is the owner's decision** ([ADR-272](ADR-272-owner-decisions-on-3d-scope.md) §5.6); [ADR-276](ADR-276-editing-in-the-scene.md) condition 1 deferred meshes to this ADR. **When it is built, and how large a first step, is the owner's to say** (§10). |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Scene Viewer edits a 3D object layer: it places a glTF model, moves, rotates and scales it, and deletes it. The scene
layer is a cache, and the models live in its **associated feature layer**. Graticula's feature service holds points,
lines and polygons. Its 3D object scene layers are extruded footprints written from a polygon layer
([ADR-269](ADR-269-scenes-web-scenes-scene-packages-and-generated-i3s.md)), and their source is edited as polygons
([ADR-276](ADR-276-editing-in-the-scene.md) §5.4).

What ArcGIS publicly documents for this (read 2026-11-07, public pages only, no SDK source; a research note, not a
specification):

- The associated feature layer is `esriGeometryMultiPatch` with `hasZ`. Editing it needs change tracking on, and *GLB*
  among its edit formats. The SDK finds it from the scene layer's portal item.
  ([SceneLayer reference](https://developers.arcgis.com/javascript/latest/references/core/layers/SceneLayer/),
  [3D object workflows](https://developers.arcgis.com/javascript/latest/3d-object-workflows/))
- The layer advertises `infoFor3D`: query and edit formats (`3D_glb`), the transform field roles, the asset and asset map
  tables, and SHA-256 as the asset hash. That is observed in a public sample service's metadata, not documented as a
  schema.
- A model is uploaded with `uploadAssets` (base64, answered with its hash). `applyEdits` adds a feature with **no
  geometry**: its attributes, its transform as fields (origin, translation, scale, axis and angle), and an `assetMaps`
  entry naming the uploaded hash. Moving, rotating or scaling changes only the transform fields. `query3D` and
  `queryAssets` give the model back
  ([applyEdits](https://developers.arcgis.com/rest/services-reference/enterprise/apply-edits-feature-service-layer/),
  [uploadAssets](https://developers.arcgis.com/rest/services-reference/enterprise/fs-upload-assets/),
  [query3D](https://developers.arcgis.com/rest/services-reference/enterprise/fs-query-3d/),
  [queryAssets](https://developers.arcgis.com/rest/services-reference/enterprise/fs-query-assets/)).
- After an edit, the SDK hides that feature's cached copy and draws it from the feature layer, up to 50,000 edited
  features, until the cache is updated.
- **Not confirmed from public pages:**
  - how T, R and S compose about the origin;
  - the exact requests the SDK's Editor sends, and in what order;
  - the flags it checks before it offers mesh editing;
  - whether it reads `extractChanges` to find edited features.

## 2. Alternatives considered

### Alternative A — the multipatch-and-assets contract, glTF only, behind our own mesh type (proposed)

- **A mesh kind of hosted layer**: a geometry column holding the feature's 3D envelope, the ESRI3DO_* transform fields,
  and two tables of our own for assets (hash → glb bytes, or a file above the document limit as
  [ADR-274](ADR-274-scene-packages-above-the-document-limit-are-files.md) does) and asset maps.
- **The REST surface the SDK uses**:
  - `uploadAssets` for `3D_glb` only;
  - `applyEdits` with `assetMaps`;
  - `query3D` and `queryAssets` returning the glb;
  - `extractChanges` over the layer's edit stamps.
  Other formats are refused, and `convert3D` is not offered. A converter from FBX, IFC, DWG or USD is a Tier 2 library,
  and needs its own ADR.
- **The cache**: the scene layer writer learns to write a mesh node from glb models (the writer already writes glTF
  geometry), and [ADR-277](ADR-277-scene-caches-rebuilt-on-schedule-and-in-part.md)'s fingerprint is the asset hash plus
  the transform.

### Alternative B — meshes as attachments on a point layer

A model stored as an attachment, placed by its point. **Rejected**: the SDK's Editor would not edit it as a mesh. It is
a different product with Scene Viewer's names on it.

### Alternative C — not now

Stay with editing footprints (ADR-276 §5.4). This is today's state, and the fallback if the prototype says the
contract cannot be met without the unconfirmed half.

## 3. Counterarguments to the preferred option

- **Half of the contract is observed, not documented.** A server built from a sample's metadata may meet a client check
  nobody wrote down. The prototype exists to find that out before the storage is designed for it.
- **It is the largest single piece left in 3D**: a new geometry kind through storage, query, edit, export and the scene
  writer, and a write path for binary assets with quotas.

## 4. Evidence

**The prototype, 2026-11-07** ([experiments/mesh-editing](../../experiments/mesh-editing/README.md), SDK 5.2.0). It
used a stub in front of the fixture server, which faked only the associated multipatch layer. Measured:

- **What the SDK needs before it treats a scene layer as editable meshes.**
  - The item relates to a feature service whose service document lists no tables. Listed tables made it a group
    layer.
  - The layer is multipatch, with `infoFor3D` in the public sample's shape.
  - The scene layer's own fields type the GlobalID `esriFieldTypeGlobalID`. Graticula's writer types it GUID, and the
    SDK refused.
- **On load** the SDK calls `extractChanges` from the cache's `serviceUpdateTimeStamp`, comparing the transform
  fields. This answers §1's unconfirmed fourth point.
- **Add.**
  1. `uploads/info`.
  2. `uploadAssets` (base64 glb).
  3. `applyEdits` with `useGlobalIds=true`: attributes and the transform fields, no geometry, and an `assetMaps.adds`
     entry naming the glb's SHA-256.
- **Move.** The model is uploaded again, and the update gives the new origin and a new asset map. A move that keeps
  the geometry sends only the transform attributes.
- **Delete.** By GlobalID.

Not measured:

- whether the Editor widget offers the layer (on the prototype's bare page it drew nothing);
- whether the edit is drawn over the cache: no request for the new feature came back in 15 seconds, so the SDK draws
  what it sent, or nothing until a rebuild;
- what another client asks for after an edit.

## 5. Decision

1. **Alternative A**, to the contract the prototype measured (§4). **The first step is the owner's: glb and a
   converter** ([ADR-281](ADR-281-owner-decisions-on-what-3d-left-partly-built.md) §5.1).
2. **The order**:
   1. a mesh kind of hosted layer with the contract above (`uploadAssets`, `applyEdits` with asset maps by GlobalID,
      `query3D`, `queryAssets`, `extractChanges`);
   2. the scene layer writer's GlobalID typed `esriFieldTypeGlobalID` for a 3D object layer;
   3. the mesh scene layer (condition 2);
   4. the converter, as its own ADR (condition 4).
3. Until a mesh layer exists, [ADR-276](ADR-276-editing-in-the-scene.md) §5.4 stands: a 3D object layer made here is
   edited as its footprints.
4. *(Built 2026-11-07.)* **A 3D object layer is its own store and FeatureServer**, apart from the hosted layers'
   pipeline, which is not touched.
   - **The item.** A document of kind `meshlayer`, listed as a `Feature Service` with the `3DObject` keyword.
   - **The store.** Its features, tombstones and asset maps are one JSON resource of the item. Each glb is a resource
     named by its SHA-256.
   - **The address.** `/rest/services/meshes/{id}/FeatureServer`. The literal folder outranks the catalog's `{folder}`
     route, so no hosted service is shadowed, and a folder named `meshes` is reserved.
   - **What it answers** is §4's list: the service and layer documents (no tables listed), `uploads/info`,
     `uploadAssets` (glb only until ADR-282's converter), `applyEdits` by GlobalID or object id with asset maps (all or
     nothing), `extractChanges` from a generation that is the edit's time in milliseconds, `query` (`1=1` and object
     ids, extents as geometry), `query3D`, `queryAssets` and the models themselves.
   - **Making one.** `POST /content/meshes` makes the layer and an empty scene layer, related as `Service2Service`.
     The scene viewer's *Create a layer to draw in* offers it as *3D objects*. *Rebuild* writes the scene layer from
     the models, in part where it can.
   - **Who may do what.** Reads follow the item's sharing; edits are the owner's and an administrator's.
   - **One writer at a time per node** (D-390).

## 6. Consequences

- **State.** None while this is a design. If built: in the catalogue, two tables per mesh layer (assets and asset
  maps); asset files above the document limit on the shared state volume, as ADR-274's packages are, so every node reads
  the same. Nothing node-local.

## 7. Assumptions this decision rests on

- The SDK 4.34's Editor edits a scene layer's meshes against any server that meets the contract in §1. `INFERRED`,
  from the documentation; not measured.

## 8. Dependencies

ADR-272 §5.6, ADR-273 (SDK 4.34), ADR-274, ADR-276, ADR-277.

## 9. Conditions

1. **The prototype**: a hand-made multipatch layer answering `infoFor3D`, `uploadAssets`, `applyEdits` with
   `assetMaps` and `query3D`, behind a scene layer whose item relates to it. It is measured whether SDK 4.34's Editor:
   - offers mesh editing;
   - places a glb;
   - moves it;
   - deletes it;
   - draws the edit over the cache.

   It also records every request the SDK sends. *(Partly discharged 2026-11-07, §4: placing, moving and deleting are
   measured and every request is recorded. The Editor widget's offer, and drawing over the cache, are not.)*
2. **The mesh scene layer**: the writer makes I3S 3D object nodes from glb assets and their transforms, and a rebuild
   picks up an edited model. *(Discharged 2026-11-07: the writer's half below, and the layer's (§5.4):
   `SceneLayerSourceTests.A_3d_object_layer_takes_a_model_from_the_scene_and_its_scene_layer_is_rebuilt_with_it` makes a
   layer from the scene, places a 10 × 20 × 10 m box through the scene layer with the SDK, finds it in the store with
   its asset map and 20 m of height and in `extractChanges`, rebuilds the scene layer with it, deletes it by GlobalID,
   and opens the item in Studio. The writer's half:* `SceneBuilding.Model` carries a glb and
   its transform. `GltfTriangles` reads the glb's triangles by the glTF specification: node transforms, indexed or not,
   with materials' base colours, and no textures or compression. The writer places them as §4 measured the SDK writing
   them: glTF's Y up, then scale, rotation about the axis, translation, from the origin. The model's own colours are kept
   per vertex. A model's footprint, base and height are its triangles' box, so the coarse levels and the partial update
   take it as a building; its fingerprint is its asset hash and transform. A GlobalID field is typed
   `esriFieldTypeGlobalID` (§4). `SceneModelTests` covers the reading, the placing, the colours, a scale, a turn and a
   move, and a moved model as a changed feature. *A rebuild from a mesh layer waits on the layer.)*
3. **An empty 3D object layer from the scene** (ADR-276 condition 2's remaining half). *(Discharged 2026-11-07: *Create a
   layer to draw in* → *3D objects*, the same test.)*
5. **The Editor widget's own placing of a model**, by a person in the scene, beside the SDK's `applyEdits` the test
   uses. The widget offers the layer (`layerInfos`); a person uploading a glb through it is not yet measured. *(Partly
   discharged 2026-11-07. Measured on SDK 5.2: with no feature template, the Editor's *Create features* was empty. The
   layer now carries one template, the transform at rest, as Esri's public sample has. The Editor lists the layer
   under *Create features* with its upload button, asserted in the end-to-end test. What is left: a person choosing a
   file in the browser's dialog and clicking where it stands, which the headless suite does not drive yet.)*
4. **The converter** from OBJ, FBX, COLLADA, IFC and USD to glb: a Tier 2 library behind our port, with its licence
   read against [ADR-180](ADR-180-the-outbound-licence-is-the-community-licence.md) and
   [ADR-254](ADR-254-gdal-without-poppler.md) (no GPL linked), and run where a malformed file cannot take the server
   down. Its own ADR. *(The ADR is [ADR-282](ADR-282-3d-models-are-converted-by-assimp-in-its-own-process.md),
   2026-11-07: Assimp's command-line tool in a process of its own, prototyped for OBJ, COLLADA and FBX. Its conditions
   carry the rest.)*

## 10. For the owner

How large the first step is: ~~glb only, placed and moved (recommended), or with the server-side conversion of other
formats~~. **Answered 2026-11-07: glb and a converter** ([ADR-281](ADR-281-owner-decisions-on-what-3d-left-partly-built.md) §5.1).
