# ADR-182 — An image service can be an elevation surface

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-13 — the direction by owner decision, the design `INFERRED` (§5a) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The ArcGIS veteran's review of the showcase's Copernicus DEM, 2026-10-13, ranked one finding above the rest: the
first things an ArcGIS user does with a terrain model — add it as ground in a 3D scene, draw a profile over it — could
not be done, because the service could not be an elevation layer. The owner was told it was larger than the review's
other findings and needed its own decision, and answered *"devam et"* (go on). That is the direction. Every design
choice below is this session's and is listed for confirmation in §5a.

What an ArcGIS client needs was measured rather than assumed: the JavaScript SDK 5.1.27 was unpacked and read, and a
headless SceneView (Brave, the SDK from js.arcgis.com 4.32) was pointed first at Esri's own Terrain3D service as a
control and then at this server.

## 2. Alternatives considered

### Alternative A — every one-band floating-point image is a surface

**Argument for.** Nothing to set; every DEM published works in 3D at once.

**Argument against.** A temperature, a rainfall total or an NDVI is a one-band float too. `Layer.fromArcGISServerUrl`
opens a LERC-tiled image service as an `ElevationLayer`, so guessing wrong turns a temperature map into a mountain
range in every client that adds it by URL.

### Alternative B — its owner says so (chosen)

**Argument for.** ArcGIS decides it the same way, at publishing: an elevation service is published as one. One flag,
off by default, so nothing published before changes.

**Argument against.** A step the publisher has to know about. The console states it beside Download, and the
services directory offers the 3D view only when it is on.

### Alternative C — a second face, an elevation endpoint beside ImageServer

**Argument for.** Imagery tiles and elevation tiles could both be served for one coverage.

**Argument against.** Not what ArcGIS clients address. Terrain3D is an ImageServer, and the SDK finds the ground by
reading that one document.

## 3. Counterarguments to the preferred option

- **The service document now differs by a flag.** `tileInfo`, `cacheType`, `serviceDataType` and the extents change
  when the flag does, so two services over the same file answer differently. That is the point, but it is a state a
  client can see change under it.
- **The extents are written in Web Mercator while `spatialReference` stays the coverage's.** Each extent names its
  reference, so no client is misled that reads it; a client that assumes the extents are in `spatialReference`
  without reading theirs would be. The SDK needs it this way (§4); Pro was not run.
- **Only the JavaScript SDK was run.** ArcGIS Pro, Scene Viewer through a portal item, and the Runtime SDKs were not.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The SDK opens an ImageServer as an ElevationLayer when its tiles are LERC and `cacheType` is absent or Elevation | `t?"LERC"!==t?.format?.toUpperCase()\|\|s&&"elevation"!==s.toLowerCase()?"ImageryTileLayer":"ElevationLayer":"ImageryLayer"` | `@arcgis/core` 5.1.27, `layers/support/arcgisLayers.js` |
| A global scene takes ground only in its own reference, which a basemap makes Web Mercator | `tilingscheme:spatial-reference-mismatch` unless the tiling reference equals the view's | `chunks/terrainUtilsSpherical.js` |
| Esri's elevation service is Web Mercator, LERC, `cacheType` Elevation, 257 × 257 tiles, 0.1 m error | Terrain3D's service document and the header of `tile/13/3099/4761` | elevation3d.arcgis.com, 2026-10-13 |
| The SDK's elevation decoder reads LERC 1 only | its worker checks for `CntZImage` and has no Lerc2 path; our Lerc2 tiles were fetched and every height came back 0 | `layers/support/LercWorker.js`; headless SceneView |
| It compares a queried point, in the tiling reference, with `fullExtent` as numbers | with `fullExtent` in degrees every point was outside and every height 0 | `layers/support/ElevationQuery.js`; headless SceneView |
| It asks `tilemap` blocks reaching past a level's grid, as Terrain3D answers them | `tilemap/1/0/0/32/32` refused here; Terrain3D answers 1,024 cells, those past the grid zero | server log; elevation3d.arcgis.com |
| With all of that, the SDK reads this server's ground | `ElevationLayer` at Uludağ 2,497.8 m (Esri's Terrain3D: 2,495.8 m), Çamlıca 263.7 m; a SceneView draws the relief | headless SceneView, local fixture, 2026-10-13 |
| The SDK's decoder reads a partial mask only as LERC 1's run-length bit mask, not as the format's other form, a grid of count blocks | every tile at the model's edge failed in a scene — `RangeError: Offset is outside the bounds of the DataView`, 51 errors on 33 tiles — while Esri's C++ decoder read the same tiles; with the mask run-length coded, none failed | ux review in a headless SceneView, 2026-10-13; rerun with the browser's console collected |
| The tiles open in Esri's own LERC decoder | `lerc.decode` on levels 3, 9 and 13: 257 × 257, values as the coverage's; masked pixels at the edge | Esri's `lerc` Python package |
| LERC 1's format | `src/LercLib/Lerc1Decode` — `CntZImage.cpp`, `BitStuffer.cpp` — Apache-2.0; the writer was written from that reading, no code taken | github.com/Esri/lerc |

## 5. Decision

An image service whose owner makes it an elevation surface (`PUT /admin/coverages/{name}/elevation`, one band only;
the console's Settings › General) is served as Esri serves Terrain3D: `serviceDataType`
`esriImageServiceDataTypeElevation`, `cacheType` `Elevation`, `keyProperties` `DataType: Elevation`, and `tile` and
`tilemap` in ArcGIS Online's Web Mercator scheme whatever the coverage is stored in, its extents written in that
scheme's reference. A tile is the coverage's heights — never its drawing or its raster function — as LERC 1, 257 × 257
samples on the tile's corner lattice, to 0.1 m. The scheme publishes its levels through the first finer than the
coverage's own pixels. `tilemap` answers a block that starts on a level's grid and reaches past its edge, the cells
past it zero. `exportImage` and every other operation are unchanged, so a 2D client still draws it as an image. The
services directory offers it in a SceneView (`/studio/map.html?face=imageserver&scene=1`).

### 5a. Inferred, for the owner to confirm

1. That it is the owner's choice per service, not inferred from the data (§2 A).
2. That the tiles are Web Mercator only, not also WGS 84 for scenes in that reference.
3. That 0.1 m, Esri's own figure, is the error a surface tile may carry.
4. That the extents move to Web Mercator for a surface and `spatialReference` does not (§3).

## 6. Consequences

**Positive.** A DEM published here is the ground of an ArcGIS 3D scene, the first thing the review found missing.
`tilemap` now answers blocks the way Esri's does for every image service, which the SDK asks at coarse levels.

**Negative.** A second LERC writer. The 3D viewer frames itself with a coarse grid of the SDK's own height queries.
Measured on the way, and not fixed here: a renderingRule chaining one function over another applied the outer one and
dropped the inner — now refused (`RasterFunction.TryParseRule`).

**State.** One column, `coverage.elevation`, migration 84; nothing held at runtime.

**Ports created.** None. `Lerc1Writer` sits beside `LercWriter` in `Graticula.Core.Coverages`.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Pro, Scene Viewer and the Runtime SDKs read an elevation image service as the JavaScript SDK does | **Unverified** — conditions 1 and 2 |

## 8. Dependencies

**Depends on** ADR-043 (ImageServer), ADR-137 (LERC), ADR-181 (the REST version).

**Depended on by** —.

## 9. Revisit triggers

- A JavaScript SDK release whose elevation decoder reads Lerc2: the second writer could go.
- A scene in WGS 84 asks for this server's ground.

## Conditions

1. **ArcGIS Pro adds the service as an elevation source of a local or global scene** and draws relief from it. Tried
   2026-10-13 through arcpy (Pro 3.7): a scene whose ground is this server's surface, built from a 2.x `.mapx` that Pro
   imports and upgrades, lists it as its elevation source — but a layout export draws a scene with this ground, with
   Terrain3D's and with none identically to the byte, so a headless export does not show whether Pro reads the
   heights. Still owed, in Pro's own window. **2026-10-14, in Pro's window, three faults before relief:** the portal
   browser failed with *Object reference not set* because a referer-bound token was refused on Pro's header-less
   `portals/self` (ADR-015 §4, narrowed); then adding the surface as ground spun every core and Pro stopped
   answering, because `pixelSizeX/Y` stayed in the file's degrees beside extents written in Web Mercator metres —
   `arcpy.Raster` read the 7200-column tile as 801,500,334 × 1,062,133,792 cells, and 7200 × 7200 once the pixel
   was written in the extents' reference. Relief itself is still unconfirmed.
2. **A portal item for an elevation surface opens in Scene Viewer as ground.** The SDK refuses a portal item of type
   *Image Service* where it expects *Image Service Elevation 3D Layer*; what this server's portal says for a surface
   has not been changed.

## 10. Dissent

None recorded.
