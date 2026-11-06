# 3D parity with ArcGIS Online and ArcGIS Enterprise

**2026-11-06, the first full pass.** ADR-270 condition 3. The owner asked for 3D *"aynı arcgis enterprise'da olduğu
gibi"* and, for the night, to compare with ArcGIS Online and Enterprise and start again on what was missing. This is
that comparison, item by item: what ArcGIS documents, what Graticula has, and for every gap whether it was built the
same night, is owed, or is the owner's to decide.

**How it was made.** The ArcGIS column is read from Esri's public documentation, listed under *Sources* and cited by
number in each row; nothing in it is from memory. ArcGIS Online's pages were read, and **Enterprise's Scene Viewer page
for 11.4** [8] — its list is a subset of Online's: Present, Navigate, Search, Layers, Basemap, Daylight, Weather, Measure
(distance and area), Slice, Elevation profile, Share, Settings and Building explorer. Every one of those is built here
but *Share*'s embed and web app (§H) and *Present*'s caption styles (§B). The Graticula column names the ADR section that shows each
claim; ADR-270 §4 names the test.

**State** is one of: *Built* (and tested), *Partly built* (the row says what is missing), *Owed* (no decision needed,
not yet done), *Owner* (a decision of scope, cost or direction — listed at the end).

## A. Authoring a scene

| ArcGIS | Graticula | State |
|---|---|---|
| A new scene is global or local, chosen at creation [1] | the same; a local scene is Web Mercator, or a 3D layer's own projected system when one is added to a new local scene (ADR-270 §5.7, §5.20) | Built |
| Layer Manager: order, visibility, group layers [2] | order, visibility, opacity, title, zoom to, remove (§5.1) | Partly built — no group layers |
| Layer style: smart mapping styles [2] | one colour, a ramp, a colour for each value, classes of a number field; a size by field, 3D shapes, extrusion (§5.1, §5.15) | Partly built — the number of values and classes is fixed, and no web style gallery |
| Layer properties: pop-ups, visibility ranges, filters [2] | pop-up title and fields; a range from Scene Viewer's named scales; a filter, tried before it is kept (§5.1, §5.9) | Built |
| Configure ground: elevation layers, how the ground is shown and navigated [2] | elevation from an elevation layer, ground opacity, underground navigation (§5.1, §5.8) | Built |
| Basemap gallery [1] | the portal's gallery, the map ground, OpenStreetMap, none | Built |
| Feature search in a scene [1] | *Search*, over the scene's feature layers; no geocoder, which this server has none of (§5.10) | Built |

## B. Slides and presentation

| ArcGIS | Graticula | State |
|---|---|---|
| Capture, rename, describe, duplicate, recapture, remove, reorder [4] | the same (§5.1, §5.9) | Built |
| A slide keeps the camera, daylight, basemap, layer visibility, weather [4] | the SDK's `Slide.createFrom` | Built |
| A slide may capture its legend, a pop-up and a slice [4] | captured with the slide and put back when it is shown (ADR-270 §5.19, SDK 4.34) | Built — the slice and pop-up halves not yet covered by a test |
| Presentation styles (caption, cover) and *Present* mode [1][4] | each slide *Shown as* caption, cover or scene only, and *Play* draws it so (ADR-270 §5.16, §5.19) | Built |
| Show or hide a slide in the presentation [4] | *In presentation*, per slide; Play skips a hidden one and a reader does not see it (§5.10) | Built |
| `#<n>` opens the scene at a slide [4] | the same (§5.9) | Built |

## C. Environment and interactive tools

| ArcGIS | Graticula | State |
|---|---|---|
| Daylight, shadows [1] | Daylight and Shadows, the SDK's (§5.2) | Built |
| Weather: rain, snow, fog [1] | Weather, the SDK's | Built |
| Measure distance, Measure area [1] | the same | Built |
| Measure volume [1] | *Volume*: cut, fill and net over an area drawn on the ground (ADR-270 §5.18, SDK 4.34) | Built |
| Slice [1] | the same | Built |
| Elevation profile [1] | the same | Built |
| Line of sight [3] | the same | Built |
| Viewshed, Dimensions [3] | *Viewshed* and *Dimensions* (ADR-270 §5.18, SDK 4.34) | Built |
| Time slider [1] | *Time*, offered with a time-aware layer (§5.9) | Built |
| Building explorer [1] | offered with a building scene layer (§5.9); on Esri's sample it opened with its levels, construction phases and disciplines | Built |
| Location: coordinates, elevation [1] | *Location*, the SDK's coordinate widget (§5.10) | Built |
| Scene Viewer settings: quality, navigation [1] | *Drawing quality*, the reader's own (§5.10); navigation is the SDK's default | Partly built |

## D. Spatial analysis from Scene Viewer

| ArcGIS | Graticula | State |
|---|---|---|
| Feature and raster analysis tools run from the scene, results added to it [5] | *Analysis*: the server's feature tools, the ones Studio's map runs, on a scene's hosted feature layer or a scene layer's own; the result joins the scene (ADR-270 §5.14) | Partly built — no raster analysis, which the server's Spatial Analysis Service does not offer |

## E. Adding layers

| ArcGIS | Graticula | State |
|---|---|---|
| Browse My content, favourites, groups, organisation, Living Atlas, ArcGIS Online [3] | the portal's search by title and kind, everything shared or only mine (§5.1) | Partly built — no favourites or groups filter; Living Atlas and ArcGIS Online are not this portal's |
| Only content within the map area [3] | *Only within the current view*: the portal's search answers `bbox` since ADR-271, and the list follows the camera | Built |
| A layer from a URL [3] | the same | Built |
| GeoJSON and CSV files [3] | from the reader's content, drawn from the document's data (§5.11); a file elsewhere on the web is not, as the console's policy names no other origin | Built |

## F. Hosted 3D layers

| ArcGIS | Graticula | State |
|---|---|---|
| Publish from a scene layer package [6][7] | ADR-269 §5.2 | Built |
| Publish from a hosted feature layer, associating the two [6][7] | 3D object and point scene layers, related as `Service2Service` (ADR-270 §5.4); the SDK finds the feature layer | Built |
| Rebuild cache: full [6] | *Rebuild* in Studio and `POST /content/scenes/{id}/rebuild` | Built |
| Rebuild cache: selected layers, partial update of what changed [6] | designed (ADR-277): nodes of changed features rewritten, once hosted layers record which rows changed | Owed — ADR-277 |
| Scheduled rebuilds, job status [6] | *Rebuild on its own when the layer changes*: a site sweep every five minutes, its last outcome on the item and in Studio (ADR-277) | Built |
| Replace a layer's package [6] | a document's *Replace file* | Built |
| Edit 3D object and 3D point layers in Scene Viewer [7] | *Edit*: the SDK's editor over the scene's editable feature layers (ADR-276) | Partly built — no mesh (3D object) editing yet, ADR-276 condition 1 |
| Create an empty 3D object layer to edit [7] | *Create a layer to draw in*: an empty hosted layer of 3D points, lines or polygons, opened in Edit (ADR-276) | Partly built — no empty mesh layer, which waits on mesh geometry |
| Levels of detail for large layers | inner nodes carry the largest buildings as simpler blocks (D-386) | Built |
| Package size | up to `ScenePackageMaxMB` (4 GB): above the document limit a package is a file in the server directories (ADR-274); Esri's 286 MB mesh sample served and drawn whole | Built |

## G. Layer types [7]

| ArcGIS | Graticula | State |
|---|---|---|
| 3D object, 3D point | served from packages, and made from feature layers | Built |
| Building | served from packages, drawn (ADR-270 §5.6) | Built |
| Mesh (integrated mesh), point cloud | served by the same path rule and drawn; Esri's whole 286 MB mesh drawn textured (ADR-274), the point cloud measured on its subset | Built |
| Voxel | served as ArcGIS serves one, and drawn in a local scene in its own system: Web Mercator and EPSG:2056 measured (ADR-270 §5.17, §5.20) | Built — a WGS 84 voxel is drawn in neither kind of scene by SDK 4.34 |
| 3D tiles, `.3tz`, Gaussian splat | a `.3tz` archive is kept, served and drawn when its content is 3D Tiles 1.1 (glTF); 1.0 (b3dm) is loaded but not drawn by SDK 4.34, and Gaussian splat has no public API before SDK 5 (ADR-278) | Partly built — waits on an SDK that draws b3dm and splats |
| Elevation layer from a DEM | an elevation image service (ADR-182), offered as the ground by its keyword (§5.8) | Built |
| Point cloud renderers: stretch, classify, unique values, RGB [7] | RGB, elevation, LAS class, intensity (§5.12); each seen drawn on Esri's sample 2026-11-07, elevation over the heights the layer's statistics give | Built |

## H. Sharing a scene [8]

| ArcGIS | Graticula | State |
|---|---|---|
| Share with groups, the organisation, everyone | a web scene is an item, shared as any item (ADR-269 §5.1) | Built |
| Email or social media link | the scene's address | Built |
| Embed in a website, create a web app from the scene | `/studio/scene-embed.html?id=…`, framable by the sites `Graticula:EmbedOrigins` lists (ADR-275) | Partly built — the embed code is offered in Studio; no web app is made from a scene |

## Still to do

Both gaps the first pass left without needing a decision were built the next night: *only content within the map
area* (ADR-271) and *analysis from the scene* (ADR-270 §5.14). What remains is in the *Partly built* rows and the owner's
list below.

## Decided by the owner, 2026-11-07

[ADR-272](../adr/ADR-272-owner-decisions-on-3d-scope.md): every item below was answered. The SDK is raised first. Large packages go on the state volume. An embed address will be framed only by origins the operator lists. 3D Tiles, `.3tz` and Gaussian splat, 3D editing and partial and scheduled rebuilds are all in scope, each owed its own ADR. The list below is kept as it was put to the owner.

## For the owner

1. **The Maps SDK version.** Measure volume, Viewshed and Dimensions are Scene Viewer tools the pinned 4.29 does not
   have. Raising `Graticula:MapSdkUrl`'s default is a change for every page that draws a map. *Recommendation:* raise it
   in its own change with the console suite run against it, then add the three tools.
2. **3D tiles, `.3tz` and Gaussian splat.** v1-scope keeps 3D Tiles out; ArcGIS now hosts them as 3D layers.
   *Recommendation:* keep them out until a user asks; they need a second tile format and textures.
3. **Editing in the scene, and empty 3D object layers.** *Recommendation:* not before the 2D editor's own open debts.
4. **Partial cache updates and scheduled rebuilds.** *Recommendation:* scheduled full rebuilds first; partial updates
   when a layer is large enough that a full one is too slow (ADR-269's revisit trigger).
5. **Embedding a scene in another site.** The console forbids being framed (`frame-ancestors 'none'`). *Recommendation:*
   an embed address of the viewer alone, framable only by origins the operator lists, as the style origins are listed.
6. **Packages above the document limit** (D-386's open half): where large packages live. *Recommendation:* the state
   volume, as each node's copy already does, with the store keeping a reference and a hash.

## Sources

1. [Get started with Scene Viewer — ArcGIS Online](https://doc.arcgis.com/en/arcgis-online/get-started/view-scenes.htm)
2. [Work with layers in scenes — ArcGIS Online](https://doc.arcgis.com/en/arcgis-online/create-maps/configure-content-in-scene.htm)
3. [Add layers to scenes — ArcGIS Online](https://doc.arcgis.com/en/arcgis-online/create-maps/add-layers-to-scene.htm)
4. [Capture slides — ArcGIS Online](https://doc.arcgis.com/en/arcgis-online/create-maps/capture-scene-slides.htm)
5. [Perform analysis in Scene Viewer — ArcGIS Online](https://doc.arcgis.com/en/arcgis-online/analyze/scene-perform-analysis.htm)
6. [Manage hosted 3D layers — ArcGIS Online](https://doc.arcgis.com/en/arcgis-online/manage-data/manage-hosted-scene-layers.htm); [Manage 3D layers — Portal for ArcGIS](https://enterprise.arcgis.com/en/portal/latest/use/manage-hosted-scene-layers.htm)
7. [3D layers — ArcGIS Online](https://doc.arcgis.com/en/arcgis-online/reference/scene-layers.htm)
8. [View scenes in Scene Viewer — Portal for ArcGIS 11.4](https://enterprise.arcgis.com/en/portal/11.4/use/view-scenes.htm)

All read 2026-11-06. Esri's documentation is cited, not copied: each row names a feature, never the page's text.
