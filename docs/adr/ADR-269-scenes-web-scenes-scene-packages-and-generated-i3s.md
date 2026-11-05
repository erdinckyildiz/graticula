# ADR-269 — Scenes: web scenes are saved, scene layer packages are served, and the server writes I3S from hosted layers

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-05. **The scope is the owner's**: asked *"3d maps için ilerleyebilir miyiz. Scenelayers?"*, and offered a web scene alone, a web scene with scene layer packages served, or both with the server writing its own I3S, they chose all three (*"1+2+3: I3S üretimi"*). **How each is built is this ADR's**, and the choices that are inference rather than the owner's words are marked `INFERRED` in §5. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Until today the only 3D this product drew was an elevation surface ([ADR-182](ADR-182-an-image-service-can-be-an-elevation-surface.md)):
an image service shown as the ground of a `SceneView` in `map.html`. There was no scene to save, no `SceneServer`, and
the migration inventory said so of every scene service it met (*"Scene services are not served by this build"*).
[v1-scope](../v1-scope.md) left the *"3D/terrain pipeline"* unbuilt and 3D Tiles out.

ArcGIS users meet 3D in three places, and each needs a different part:

1. **A web scene** — a saved document of layers, ground, camera and slides, the 3D twin of a web map. Pro, the Maps SDK
   and Scene Viewer read and write it as a portal item of type `Web Scene`.
2. **A scene layer** — a `SceneServer` serving I3S: a tree of nodes, each carrying meshes or points and their
   attributes, which a client loads by level of detail. Pro writes one as a **scene layer package** (`.slpk`), a zip of
   exactly the resources a service serves.
3. **Making one** — a scene layer is normally built by Pro or Enterprise from 3D features. A server that only serves
   packages leaves every user without Pro unable to have one.

I3S is a published standard: OGC Community Standard 17-014r9, with Esri's specification public on GitHub
(`Esri/i3s-spec`, CC BY-ND 3.0). Implementing a format from its published specification is what [§5 of the master
prompt](../../MASTER_GIS_PLATFORM_PROMPT.md) permits; the specification's text is not copied into this repository.

## 2. Alternatives considered

### Alternative A — all three, with generation writing packages (chosen)

**Argument for.** One serving path: a package Pro wrote and a package this server wrote are served by the same code,
so what the server generates is checked by the same client that reads Pro's. The generator's output is a file that
can be downloaded and opened in Pro, which is the strongest test of it there is.

**Argument against.** The generator is a new engine — tree building, mesh building, attribute buffers — and the part
the scope document said was not built.

### Alternative B — generation served from the database, without a package

**Argument for.** No second copy of the data; an edit could reach the scene without rebuilding.

**Argument against.** Every node request becomes a query and a mesh build; the I3S tree is a cache by design, and a
live tree is a server doing on every request what Pro does once. An edit making a scene stale is the ordinary I3S
contract (a scene layer is rebuilt or *refreshed*), not a defect to design around in the first version.

### Alternative C — web scenes only, and scene layers left to Enterprise

**Argument for.** A day's work, no new engine.

**Argument against.** The owner chose otherwise; and a web scene with nothing 3D to put in it but extruded features
and a ground is half a feature.

## 3. Counterarguments to the preferred option

A package per scene layer is a second copy of the data that goes stale on the first edit. A generated scene layer will
look worse than Pro's — no textures, simple level-of-detail — and a user comparing the two will notice. The Maps SDK
draws the viewer, so scenes stay on Esri's CDN ([ADR-253](ADR-253-openlayers-draws-2d-and-the-maps-sdk-draws-scenes.md)
accepted that for scenes already).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The I3S resources, their URLs and the package layout are public | the specification, versions 1.6 to 2.1 | `github.com/Esri/i3s-spec` (read 2026-11-05) |
| A web scene saved in Studio is a `Web Scene` item and opens again with its layers | saved, reopened by id, item type read back | `SceneViewerTests.A_scene_is_saved_and_opens_again_with_its_layer` |
| Packages Pro wrote are served and drawn | ArcGIS Pro 3.7's `CreatePointSceneLayerPackage` and `Create3DObjectSceneLayerPackage` (both Draco): the Maps SDK 4.29 loaded 20 points and 12 objects through `/rest/services/hosted/{id}/SceneServer` | measured 2026-11-05, local fixture |
| A layer the server writes is drawn | eight footprints of the fixture's `ci_buildings`, floors × 3 m: 8 features loaded by the SDK, drawn as raised blocks | `SceneViewerTests.A_polygon_layer_is_made_a_scene_layer_and_opened_in_the_viewer`, `AScenePackageIsServedAndMadeTests` |
| The writer's buffers and package are what the specification and Pro write | geometry header and face ranges, string and Float64 buffers (the 4-byte pad measured in Pro's package), the hash table's offsets, the root box's rotation against Pro's | `SceneLayerPackageWriterTests` |
| WebAssembly is needed for Draco | without `'wasm-unsafe-eval'` every node of Pro's packages was fetched and none drawn; the browser console named the policy | measured 2026-11-05 |
| The Draco this server writes is read by Google's decoder and by the Maps SDK | every node of a 5,000-point package decoded by Google's reference decoder (its Python binding, used as a test oracle outside the repository), positions and ids back to the input; the Maps SDK drew the 5,000 points at full extent and loaded one leaf of 1,253 when zoomed in; the fixture's `ci_EarlyAlert` made into a point scene layer and drawn | measured 2026-11-05; `PointSceneLayerTests`, `A_point_layer_becomes_a_point_scene_layer_of_draco_geometry`, `SceneViewerTests.A_point_layer_is_made_a_point_scene_layer_and_drawn` |
| Pro's point layer layout | Draco bitstream 2.3, a kd-tree point cloud; POSITION as metres from the node's centre with `i3s-scale_x`/`i3s-scale_y` in its metadata; a UINT32 GENERIC attribute with `i3s-attribute-type = feature-index` and the ids as `i3s-feature-ids`; `pointNodePages` with `maxScreenThreshold`; the plural `geometryDefinitions` | read from Pro's package with Google's decoder, 2026-11-05 |

## 5. Decision

1. **A web scene is a portal item of type `Web Scene`**, kept as an app item is ([ADR-264](ADR-264-an-app-or-a-link-is-an-item-and-an-item-keeps-resources.md)):
   its JSON is its content, `addItem`, `update` and `data` read and write it, and it is shared, moved, protected and
   recycled as any item. Studio opens it in a scene viewer drawn by the Maps SDK, which saves through the portal.
2. **A scene layer package is served as a `SceneServer`.** A `.slpk` added as an item and published becomes a hosted
   scene service; its resources are read from the package as the I3S REST paths ask for them.
   *(Built 2026-11-05, and not as first inferred: the package is a **document** (ADR-188) of a new kind, listed as the
   `Scene Service` it serves at `/rest/services/hosted/{item id}/SceneServer`, so its owner, sharing, groups,
   protection and recycle bin are a document's. Pro's `addItem` of a `Scene Package` lands there. Each node writes the
   document's bytes once to `{StatePath}/scene-packages/` and answers I3S paths from that copy by seeking in the zip,
   with its own reader of the central directory (ZIP64 included); a gzipped entry goes out as it is, with
   `Content-Encoding: gzip`. The service document is this server's: I3S 1.7+ does not define it.)*
   *(The console's content security policy gained `'wasm-unsafe-eval'`: the Maps SDK decodes Draco geometry — every
   package Pro writes — in WebAssembly. It admits compiling WebAssembly and nothing else.)*
3. **The server writes I3S from a hosted feature layer**: a point layer becomes a point scene layer, and a polygon layer
   with a height field becomes a 3D object layer of extruded footprints. What it writes is a package, served by 2.
   *(INFERRED: untextured, version 1.7+ node pages, WGS 84 global scene; rebuilt on request, not on every edit.)*
   *(Built 2026-11-05: `POST /content/scenes` with the layer, a number field for the height and metres per unit of it
   (3 for floors), an optional base field and a title; Studio offers it as **Create scene layer…** on a polygon layer's
   More menu. The writer (`Graticula.Core/Scenes`) is a quadtree of at most 512 buildings a node, the root without
   geometry as I3S requires, boxes aligned to east, north and up at their centres, and the uncompressed 1.7 geometry
   buffer Pro writes first — positions, colours, feature ids, face ranges, no normals. Footprints are triangulated by
   ear clipping with holes bridged in. A layer is read in pages up to 200,000 footprints and must fit the document
   limit.)*
   *(Point layers too, from 2026-11-05 ([D-386](../architecture-debt.md)): I3S 1.7+ allows a point layer's geometry only
   Draco-compressed, so the server writes Draco — **its own encoder, written from Google's public bitstream
   specification, not a library**. It writes the one encoding every decoder must read, a sequential point cloud whose
   attributes are raw values, and none of Draco's compression; gzip, which every package entry has, takes back much of
   the difference. That is Tier 2 work done in-house rather than adopted, and it is chosen because the subset is about
   a hundred lines against a native library on every platform the server runs on; a layer that needs real compression
   is the trigger to adopt one behind a port. A point is placed at its height above the ground from a field, at its own
   z, or at zero above the ground — never `onTheGround`, in which the Maps SDK drapes an icon flat on the terrain. The
   tree is `AutoThinning`: nodes above the leaves carry one point per cell of a 32 × 32 grid of what is below them, so
   points thin out with distance instead of every leaf in view being loaded. Ids are 32-bit, as the point profile's
   `i3s-feature-ids` are; a layer with wider ids is refused, not renumbered.)*

## 6. Consequences

**Positive.** Scenes can be saved, shared and opened by ArcGIS clients; Pro's scene packages have a server; a user
without Pro can turn their buildings into a 3D layer.

**Negative.** A generated scene layer is a snapshot and goes stale on edit until rebuilt. Packages take disk. The
viewer needs `js.arcgis.com`.

**Ports created.** None planned: the package reader and writer are Tier 1 (the service model and the tiling pipeline).

**State.** Packages on the state volume (2), a document per web scene (1), and a package per generated layer (3).

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | The Maps SDK draws an I3S 1.7+ package whose nodes carry uncompressed geometry and no textures | Unmeasured — condition 3 |

## 8. Dependencies

**Depends on:** ADR-182, ADR-253, ADR-264, ADR-188 (documents).

**Depended on by:** —

## 9. Conditions

1. **A web scene saved in Studio opens in the Maps SDK, and one saved by Pro opens in Studio** — *(2026-11-05: the
   first half by the Maps SDK, which is what Studio's viewer is — `SceneViewerTests`. Pro saving one and Studio opening
   it is owed; the desktop was locked when it was to be run.)*
2. **A package Pro wrote is served and drawn** by the Maps SDK and by Pro. *(2026-11-05: by the Maps SDK — Pro's point and
   3D object packages, 20 and 12 features loaded. By Pro: owed, the desktop locked.)*
3. **A layer the server generated is drawn** by the Maps SDK and by Pro, and its package opens in Pro. *(2026-11-05: by
   the Maps SDK, measured and tested, buildings and points both. One was made on dev from `look_buildings` for Pro to
   add; Pro is owed, and for a point layer as well.)*
4. **Every new screen passes the ux-designer review**, first-run state included. *(Open.)*

## 10. Revisit triggers

- A request for textured or integrated-mesh layers.
- A generated layer large enough that rebuilding it on request is too slow.

## 11. Dissent

None recorded.
