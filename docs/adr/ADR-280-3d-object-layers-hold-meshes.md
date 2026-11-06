# ADR-280 — A 3D object feature layer holds meshes, and the scene edits them

| | |
|---|---|
| **Status** | `REQUIRES PROTOTYPE` |
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

None yet. This ADR is a design and a list of what the prototype has to measure (§9).

## 5. Decision

None until the prototype (§9 condition 1). Until then [ADR-276](ADR-276-editing-in-the-scene.md) §5.4 stands: a 3D
object layer made here is edited as its footprints.

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

   It also records every request the SDK sends. *(Open.)*
2. **The mesh scene layer**: the writer makes I3S 3D object nodes from glb assets and their transforms, and a rebuild
   picks up an edited model. *(Open.)*
3. **An empty 3D object layer from the scene** (ADR-276 condition 2's remaining half). *(Open.)*

## 10. For the owner

How large the first step is: **glb only, placed and moved** (recommended), or with the server-side conversion of other
formats, which needs a converter library (Tier 2, its own ADR and licence check).
