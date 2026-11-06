# ADR-276 — Editing in the scene: the first step

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-07. **That editing starts now is the owner's decision** ([ADR-272](ADR-272-owner-decisions-on-3d-scope.md) §5.6). Where the first step stops is this ADR's. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Scene Viewer edits a scene's feature layers in 3D, and it edits 3D object layers: hosted scene layers whose associated
feature layer holds **meshes**. Graticula's feature service holds points, lines and polygons. It has no mesh geometry,
no multipatch, and no glTF upload. The scene viewer could not edit anything, and the SDK's writes carried no token,
which the server requires for a write (ADR-023 §4c).

## 2. Alternatives considered

### Alternative A — the SDK's editor over the scene's editable feature layers now; meshes as their own ADR (chosen)

*Edit* opens the Maps SDK's `Editor` over the scene's feature layers whose service says this reader may edit them
(`capabilities.operations.supportsEditing`). Features are placed in the scene, with their height where the layer keeps
z. The SDK's requests to this server carry the tab's token, as the console's map's already do. The server decides who
may edit, and nothing changes there.

### Alternative B — mesh editing first

A 3D object layer needs a mesh geometry in the feature service, stored, queried and served, and a scene cache rebuilt
from it. That is a new geometry type through the whole stack. **Deferred to its own ADR** (§9).

## 3. Counterarguments to the preferred option

- **It is not yet what *edit 3D object layers* means in ArcGIS.** True, and the parity table says so. What it does
  give is every editable point, line and polygon layer in a scene, which Scene Viewer edits too.

## 4. Evidence

`SceneViewerToolsTests.An_editable_layer_is_edited_in_the_scene` (console, on the fixture 2026-11-07):

- an editable hosted layer added to a scene offers *Edit*;
- the editor opens with that layer enabled;
- an edit sent through the SDK's `applyEdits` is accepted with the reader's token;
- the edit is removed again.

## 5. Decision

1. *Edit* is a scene tool, offered while the scene holds a layer the reader may edit, never in the embed view.
2. The SDK's requests to this origin carry the tab's token (`esriConfig.request.interceptors`).
3. Editing a scene layer made here goes through its feature layer, in Studio's map or this scene, followed by
   *Rebuild*. The scene layer itself is a cache.

## 6. Consequences

- **State.** None.
- A scene layer made from an edited feature layer is stale until rebuilt. Scheduled and partial rebuilds are the
  owner's other decision (ADR-272 §5.7).

## 7. Assumptions this decision rests on

- The SDK 4.34 `Editor` in a `SceneView` edits a feature layer's points, lines and polygons. That was measured for
  points; lines and polygons follow the same widget.

## 8. Dependencies

ADR-272 §5.6, ADR-273 (SDK 4.34), ADR-023 §4c.

## 9. Conditions

1. **Meshes**: a 3D object layer's feature layer holds mesh geometry. It is edited in the scene and rebuilt into its
   scene layer. This needs its own ADR. *(Open.)*
2. **An empty 3D object or 3D point layer is created from the scene**, ready to edit, as Scene Viewer's *Create layer*
   does. *(Open.)*
3. **Editing through a scene layer made here**: the scene layer's associated feature layer is offered to the editor,
   and the cache is rebuilt afterwards. *(Open.)*
