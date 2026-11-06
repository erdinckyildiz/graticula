# ADR-278 — A 3D Tiles archive is served from its zip and drawn in a scene

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-07. **3D Tiles, `.3tz` and Gaussian splat are in scope by the owner's decision** ([ADR-272](ADR-272-owner-decisions-on-3d-scope.md) §5.5). This is the first step, the archive. The address, the item type and the reading are this ADR's. |
| **Supersedes** | v1-scope's *"3D Tiles stays out"*, as far as archives go (ADR-272 amended the row). |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

OGC 3D Tiles is a tileset (`tileset.json`) and the tiles it names: glTF in 3D Tiles 1.1, and b3dm and its siblings in
1.0. A **3D Tiles archive** (`.3tz`) is those files in one zip, tileset at the root. ArcGIS Online hosts one as a 3D
tiles layer, and the Maps SDK 4.34 draws a tileset with `IntegratedMesh3DTilesLayer` ([ADR-273](ADR-273-the-maps-sdk-is-4-34.md)).
A scene layer package is the same kind of thing for I3S, and this server already keeps one as a document and serves
it by entry from its zip ([ADR-269](ADR-269-scenes-web-scenes-scene-packages-and-generated-i3s.md)).

## 2. Alternatives considered

### Alternative A — the archive as a document, served by path from its zip, as a scene layer package is (chosen)

A `.3tz` (a zip so named) is a document kind. Above the document limit it is a file, as
[ADR-274](ADR-274-scene-packages-above-the-document-limit-are-files.md) keeps a large package. It is listed as a
**`3D Tiles Service`** item whose `url` is `…/rest/services/hosted/{id}/3DTilesServer/tileset.json`. Every path below
`3DTilesServer/` is the archive entry of that path, served as it is: nothing is rewritten, and a path with `..` is
refused. The same reader indexes the zip once and seeks per request. The scene adds it from the list by its kind, or
from an address naming a tileset or a `3DTilesServer`, as the SDK's 3D tiles layer.

### Alternative B — unpack the archive into a folder of tiles

**Rejected.** It doubles the storage and adds a second copy to keep consistent. The zip is already seekable.

### Alternative C — convert 3D Tiles to I3S

**Rejected.** It is a converter, which is Tier 3 territory in spirit and large in fact, and the SDK draws 3D Tiles
directly.

## 3. Counterarguments to the preferred option

- **Only what the SDK's 3D tiles layer draws is drawn.** That is 3D Tiles 1.1 with glTF content; a b3dm-only 1.0
  tileset may load and draw nothing. That is recorded as condition 2 rather than claimed.

## 4. Evidence

`TilesArchiveTests` (console, on the fixture 2026-11-07). The test makes its own archive, one 50 m glTF box placed by
the tileset's east-north-up transform, so nothing of anyone else's is in it.

- The archive is uploaded and listed as `3D Tiles Service` with its tileset address.
- `tileset.json` is served, and a `..` path is refused.
- The box is served as `model/gltf-binary`.
- In a scene, the archive is offered among *3D tiles layers*, added, loaded as `integrated-mesh-3dtiles`, and its view
  settles. The server's log shows the SDK fetching `box.glb`. It fetches from a worker, which the page's own timings
  do not list. The box was seen drawn on a screenshot.

## 5. Decision

1. Document kind `3tz`, recognised by a zip head and the `.3tz` name, listed as `3D Tiles Service`. `INFERRED`: that is
   the name ArcGIS Online gives a hosted 3D tiles layer.
2. `GET /rest/services/hosted/{id}/3DTilesServer/{path}` serves the entry at that path, under the item's sharing.
3. Above the document limit, the archive is a file (ADR-274), checked to hold a `tileset.json`.
4. The scene viewer adds it as `IntegratedMesh3DTilesLayer`. The web scene keeps it as the SDK writes it.

## 6. Consequences

- **State.** None new. It is a document, and a file when it is large.
- The portal's `bbox` search does not find an archive yet: its extent is the tileset's root bounding volume, which is
  not read (ADR-271 finds only items with an extent).

## 7. Assumptions this decision rests on

- The SDK 4.34's 3D tiles layer reads tilesets from any origin it may fetch from. Measured for this server.

## 8. Dependencies

ADR-269, ADR-272 §5.5, ADR-273, ADR-274.

## 9. Conditions

1. **A real-world archive** (a city's photogrammetry exported as `.3tz`) is drawn, and its size and node count are
   recorded. *(Open.)*
2. **3D Tiles 1.0 content** (b3dm): measured whether the SDK draws it, and the answer said where a 1.0 archive is
   added. *(Open.)*
3. **Gaussian splat**: the SDK 4.34 has `GaussianSplatLayer`. What it reads, and whether this server can host it the
   same way, needs its own measurement. *(Open.)*
4. **An archive's extent** from its root bounding volume, for the portal's `bbox` search. *(Open.)*
