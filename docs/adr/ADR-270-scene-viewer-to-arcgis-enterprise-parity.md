# ADR-270 — Scenes to ArcGIS Enterprise's level: Scene Viewer's tools, and what a scene layer is made from

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-05, overnight. **The direction is the owner's**: *"aynı arcgis enterprise'da olduğu gibi"* ("just as it is in ArcGIS Enterprise"), and for the night *"buradaki işlerin tamamını bitir. sonra git arcgis online ve enterprise ile kıyasla. eksikse tekrar başla."* ("finish all the work here; then compare with ArcGIS Online and Enterprise; if something is missing, start again"). **Which parts of Enterprise that sentence takes in is this ADR's reading of it**, and every such part is marked `INFERRED` in §5 and listed for the owner's confirmation. |
| **Supersedes** | — |
| **Superseded by** | — |
| **Confirmed** | 2026-11-07: the owner confirmed every `INFERRED` reading here (§5.1–5.17) — [ADR-272](ADR-272-owner-decisions-on-3d-scope.md) §5.8. The markers are left in place so the reading can be told from the confirmation. |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[ADR-269](ADR-269-scenes-web-scenes-scene-packages-and-generated-i3s.md) gave Graticula web scenes, a `SceneServer` for
scene layer packages and a generator for two kinds of scene layer. Its viewer drew a scene and let its owner list,
hide, add and remove layers, choose an elevation surface and save — and nothing else. ArcGIS Enterprise's Scene Viewer,
which an ArcGIS user compares it with, is a designer: a layer's look and pop-up, slides, the basemap, the ground, and a
set of 3D analysis tools, all kept in the web scene it saves so that Pro and the Maps SDK open the same thing.

The owner asked for the same. *The same* is not a list, and Enterprise's 3D is large: Scene Viewer, hosted scene layers
of five profiles, scene layer caches tied to feature layers, elevation layers, local scenes, 3D Tiles, textures. This
ADR records which of those the sentence is read as covering, how each is built, and which it is not read as covering.

## 2. Alternatives considered

### Alternative A — Studio's own panel for what edits the scene, the SDK's widgets for what draws in it (chosen)

**Argument for.** What the panel edits — layer order, a layer's renderer and pop-up, slides, basemap, ground — is the web
scene document itself, and the panel already speaks Studio's design language, keyboard pattern and copy; its state is
plain DOM that the console suite reads. The analysis tools (measurement, line of sight, slice, shadow cast, daylight,
weather, elevation profile) draw into the 3D view and are the SDK's own work; reimplementing any of them would be
writing a renderer. Each is loaded the first time it is opened.

**Argument against.** Two visual languages on one page: Studio's panel and the SDK's widgets.

### Alternative B — the SDK's widgets for everything (LayerList, BasemapGallery, Slides via Expand)

**Argument for.** Least code; the SDK's LayerList already has visibility, order and opacity.

**Argument against.** The SDK has no slide editor and no pop-up or renderer editor — Scene Viewer's own designer panels
are not part of the public SDK — so those would be Studio's anyway, beside SDK widgets for the rest. The gallery widget
reads basemaps from the SDK's portal sources, and the SDK's OpenStreetMap layer asks three subdomains the console's
policy does not admit (ADR-269's note in `scene.js`).

### Alternative C — keep the viewer minimal; scenes are authored in Pro

**Argument for.** No designer to maintain; Pro writes web scenes and the server already keeps them.

**Argument against.** The owner asked otherwise, and a user without Pro then has a viewer that can show a scene and not
make one worth showing.

## 3. Counterarguments to the preferred option

The look editor writes only a simple renderer with up to two visual variables, where Scene Viewer offers types,
classes, unique values and its whole style gallery: a scene styled in Pro keeps its renderer until somebody picks a look
here, and then loses it. The analysis widgets are the SDK's styling inside a Studio page. Every SDK widget is another
fetch from `js.arcgis.com` (ADR-253's dependency, widened rather than created).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A layer's order, opacity, extruded look by a field and pop-up title are saved in the web scene and read back | saved through the portal, `operationalLayers` read from the item's data: order, `opacity` 0.5, an `Extrude` symbol layer with a `sizeInfo` of `$feature.floors * 3`, `popupInfo.title` `{floors}`; reopened, the form reads the look back | `SceneViewerToolsTests.A_layers_order_opacity_look_and_popup_are_kept_in_the_scene` |
| Slides, the basemap and the ground are saved and read back | three captured, one renamed, one deleted, reordered, played; `presentation.slides` 2 with the renamed title; `baseMap.baseMapLayers` empty for None; `ground.transparency` 50 and `navigationConstraint.type` `none`; reopened, the panel shows both slides and the ground's settings | `SceneViewerToolsTests.Slides_the_basemap_and_the_ground_are_kept_in_the_scene` |
| Each of the eight analysis tools opens in the scene, and the legend follows the layers | each tool's SDK widget drawn in the tool panel, one at a time; the legend names the added layer; the add list narrows by kind and title | `SceneViewerToolsTests.Every_analysis_tool_opens_and_the_legend_and_search_answer` |
| A scene layer made here is found by the Maps SDK as the associated layer of its feature layer | the SDK 4.29, given the scene layer by its item, asked `relatedItems?relationshipType=Service2Service&direction=reverse` of the scene layer's item (read from the server's request log), then loaded the feature layer as `associatedLayer`; with the relationship kept the other way it found none | `SceneLayerSourceTests.A_made_scene_layer_names_its_feature_layer_and_the_sdk_finds_it`, measured 2026-11-05 |
| A Scene Service item's data must be JSON | the SDK, adding a scene layer by its item, read the item's `data` and failed on the package's bytes: *Unexpected token 'P', "PK…" is not valid JSON* | measured 2026-11-05 |
| Rebuild replaces the package in place | made from `ci_buildings`, rebuilt from Studio's page: *rebuilt with 8 buildings*, the item's `modified` later, its `SceneServer` still answering; a package without a source refused with 409 | `SceneLayerSourceTests.Rebuild_makes_the_scene_layer_again_in_place_from_studio`, `…A_package_not_made_here_has_no_source_to_rebuild_from` |
| A building scene layer package is served and drawn | ArcGIS Pro's *Admin_Building_v18* — the building scene layer sample the I3S specification links to (Esri's item `3e6417f6544b422496a022f6e84aeaaa`, 31.7 MB, 22,129 entries, 37 sublayers), uploaded to a local fixture: the Maps SDK 4.29 loaded it as a `BuildingSceneLayer`, every sublayer document and node page answered 200, 52 Draco node geometries of its *Overview* sublayer were fetched, and once its view stopped updating (34 s in software WebGL) it held 509 features, drawn. The package has no licence stated and is not in this repository | measured 2026-11-06; `ScenePackagePathsTests` pins the paths |
| A local scene keeps its type and its clip | made local, a feature layer projected into it, clipped to the view, saved: `viewingMode` `local`, `clippingArea.clip` true with an extent, the item's `ViewingMode-local`; reopened local and clipped; a WGS 84 scene layer refused with the reason | `SceneViewerToolsTests.A_local_scene_is_clipped_and_kept` |
| Only an elevation surface is offered as the ground, and it is kept | the fixture's `ci_imagery` not offered; made a surface, offered, chosen, saved as an `ArcGISTiledElevationServiceLayer` naming it | `SceneViewerToolsTests.An_elevation_surface_is_offered_as_the_ground_and_kept` |
| A layer's range and filter, a slide's description, a duplicate and `#n`, and Time are kept or offered | `minScale` 144448 and `definitionExpression` `floors >= 0` in the saved layer; a refused filter said and not kept; a description and a `(copy)` slide saved; `#2` opened at the second slide; Time offered only with `ci_observations` and opened | `SceneViewerToolsTests.Ranges_filters_slide_descriptions_and_the_time_tool_are_kept` |
| An integrated mesh and a point cloud package are served and drawn | Esri's *Rancho_Mesh_v18* (item `95a427c7…`, 286 MB) and *Morro_Bay_LiDAR* (item `496552d0…`, 578 MB), each cut to its root and four children (26 MB, 0.4 MB) because both exceed the document limit: the SDK loaded an `IntegratedMeshLayer`, fetched four geometries and four compressed textures (`textures/0_0_1` → `.bin.dds.gz`) and drew the mesh textured; it loaded a `PointCloudLayer`, fetched four `.bin.pccxyz` geometries and `.bin.pccrgb` colours through the stem lookup and drew the points in colour. Neither package is in this repository | measured 2026-11-06 |
| A CSV and a GeoJSON file from content are added and kept | both uploaded, offered under their kind, added as `csv` and `geojson` layers, saved with `layerType` `CSV` (its URL the document's data) and `GeoJSON` | `SceneViewerToolsTests.A_csv_or_geojson_file_from_content_is_added_and_kept` |
| A point cloud's looks are applied and kept | on the cut *Morro_Bay_LiDAR*: all four offered, each applied as `point-cloud-stretch`, `point-cloud-unique-value`, `point-cloud-stretch` and `point-cloud-rgb` with no page error, the scene saved with a `pointCloudRGBRenderer`. **Not seen drawn** in each look — the one picture taken was of the camera too close — and not in a committed test, the sample not being in the repository | measured 2026-11-06 |
| ADR-269's scene tests still pass on the new viewer | the three `SceneViewerTests` | run 2026-11-05 against a local fixture, Chromium 156 with SwiftShader |

## 5. Decision

1. **Scene Viewer's designer, in Studio's panel** — `scene.html`'s panel becomes six tabs: *Layers*, *Add*, *Basemap*,
   *Ground*, *Slides*, *Legend*. *(INFERRED: that the sentence covers Scene Viewer's designer as a whole, and that these
   six are its parts.)*
   - **Layers**: visibility, order (▲▼, top of the list drawn on top), opacity, a title in this scene, zoom to, remove;
     and per layer a **look** — polygons flat or extruded, points an icon or a 3D shape (sphere, cylinder, cone, cube,
     diamond), lines a line or a tube, 3D objects a solid colour; one colour or a colour ramp by a number field; a size
     or height in metres, or from a field times metres per unit (ADR-269's floors × 3) — its **height** (draped, above
     the ground, at its own z, on top of the scene; an offset) and its **pop-up** (on or off, a field as its title, the
     fields shown). Each is the layer's own renderer, `elevationInfo` and `popupTemplate`, so a web scene keeps it as
     Pro and the Maps SDK write it. *(INFERRED: a simple renderer with a size and a colour variable is enough of Scene
     Viewer's styles for v1; types, classes and the web style gallery's 3D models are not built.)*
   - **Add**: the portal's own search, by title, by kind (scene, feature, imagery, map image, vector tile) and *only my
     content*, or a service address.
   - **Basemap**: the portal's basemap gallery (ADR-184), the operator's map ground, OpenStreetMap, none — the list
     `webmap.js` offers.
   - **Ground**: an elevation surface (ADR-182), the ground's opacity, and whether the camera may go below it.
   - **Slides**: capture, rename, reorder, update to the current view, delete and play, kept as the web scene's
     `presentation.slides`.
   - **Legend**: the SDK's.
2. **Analysis tools on the scene** — distance, area, line of sight, slice, shadows, daylight, weather and an elevation
   profile, the SDK's widgets, one open at a time in a panel under a toolbar on the scene; on a phone the toolbar folds
   behind one button. *(INFERRED: these eight are what Scene Viewer's analysis means; the SDK's viewshed and
   volume-measurement tools are not in 4.29 and are not built.)* A measurement is cleared when its tool closes.
3. A reader who is not the scene's owner sees every tab but *Add*, can switch layers, play slides and use every tool,
   and is offered nothing that changes the scene: where the owner has controls, the reader has a sentence saying what
   the scene's basemap and ground are.
4. **A scene layer made here keeps where it came from, and is made again from it** — Enterprise's *associated feature
   layer* and *Rebuild cache*. *(INFERRED: that "the same as Enterprise" takes in the link between a hosted scene layer
   and its feature layer and the cache rebuilt on request; Enterprise's partial *Update cache* of edited nodes only is
   not built — a rebuild is whole, which is seconds for a district.)*
   - The request that made it is kept as the scene layer's private item resource `graticula/scene-source.json`, and the
     feature service is related to it as ArcGIS's **`Service2Service`, from the feature layer to the scene layer** — the
     direction the Maps SDK asks for (§4). Migration 133 admits the type. A scene layer added **by its item** — from the
     viewer's list, or opened from its own page — then has the feature layer as its `associatedLayer`, so queries and
     statistics on it go to the live layer.
   - `POST /content/scenes/{id}/rebuild` makes it again from the layer as it now is and replaces the package in place:
     its id, address, sharing and every scene that uses it stay, and each node's copy of the package is keyed by the
     document's modified time (ADR-269 §5.2), so none is read stale. `GET /content/scenes/{id}/source` says what it was
     made from, to its owner and administrators only. A package uploaded rather than made here has no source and is
     refused a rebuild with the reason.
   - Studio's page of such a scene layer says what it was made from and when, says when the layer has been edited since
     (the layer's own `editingInfo`), and offers **Rebuild**.
   - A scene layer package's portal `data` is now `{}` — a Scene Service item's data is its layer's settings, and the
     SDK reads it as JSON — and the package downloads from the document's own address, as Studio's Download always did.
6. **Every scene layer profile a package carries is served by one path rule** — *(INFERRED: that "the same as
   Enterprise" takes in hosting Pro's building, integrated mesh, point cloud and voxel packages, not generating them.)*
   A building scene layer's sublayers are scene layers under `sublayers/{id}` — their documents, node pages, node
   documents and shared resources at any depth — the layer answers at its document's own id as well as 0 (I3S says a
   client must not assume a building layer's id is 0), and a resource stored under an extension the rule does not name
   (a point cloud's `.bin.pccxyz`, a voxel layer's) is found by its path without one.
   *(Measured for a building scene layer, an integrated mesh and a point cloud (§4). The mesh and point cloud samples the
   specification links to are 286 MB and 578 MB, over the document limit — D-386's open half — so each was measured on
   a subset of itself: its root and the root's four children, the node page cut to them. Voxel: no sample found.)*
7. **A scene is global or local**, chosen while it is new and empty, as Scene Viewer asks it; a local scene is flat, in
   Web Mercator — the projection of every basemap this server offers, so its ground draws, and feature layers are
   projected to it by the SDK — and may be **clipped** to the current view or shown whole. The web scene keeps
   `viewingMode` and `clippingArea` (`{ clip, geometry }`), and its item the `ViewingMode-local` keyword.
   *(INFERRED: that local scenes are in the sentence. Not built: a local scene in another projected system, and
   projected scene layers — the server writes I3S in WGS 84, which I3S shows in a global scene only, so a local scene
   refuses such a layer and says why rather than leaving it undrawn.)*
8. **The ground is an elevation layer, as Enterprise's tiled elevation layer is** — ADR-182 already serves a DEM as
   Terrain3D does (LERC tiles in Web Mercator, the `Elevation 3D Layer` keyword). Compared with Enterprise, what was
   missing was the viewer's: it offered every image service as the ground, a picture as readily as heights. It now
   offers those with the keyword, says when there are none, and keeps the choice as an
   `ArcGISTiledElevationServiceLayer`. Enterprise builds an elevation layer's tile cache ahead; this server draws each
   tile on request — a difference of cost, not of what a client sees. *(INFERRED: that this is the comparison the night
   asked for — §6's task — and that building a cache ahead is not needed for parity.)*
9. **What the comparison with ArcGIS Online found missing, and was cheap** ([3d-parity-arcgis.md](../reviews/3d-parity-arcgis.md)):
   a layer's **visible range**, from Scene Viewer's named scales, and a feature layer's **filter**, tried against the
   layer before it is kept so a condition it refuses is said; a slide's **description**, **Duplicate**, and Scene
   Viewer's **`#n` address** of a slide; **Time**, offered while a time-aware layer is in the scene, and **Building
   explorer**, while a building scene layer is.
10. **A slide may be left out of the presentation** — Scene Viewer's *Hide in presentation*, the SDK's `Slide.hidden`:
    Play skips it, and a reader does not see it. **Location, Search and drawing quality** — the SDK's coordinate widget; its search widget over the scene's own feature
    layers, with no geocoder (this server has none); and the reader's quality, kept in their browser, not the scene.
    *(INFERRED, as §5.1–5.9: that these are in the sentence. What the comparison left to the owner — an SDK upgrade for
    volume, viewshed and dimensions; 3D tiles; editing in the scene; partial and scheduled rebuilds; where packages above
    the document limit live — is listed at the end of the comparison with a recommendation each.)*
11. **A CSV or GeoJSON file in the reader's content is added to a scene** — Scene Viewer's adding a file: the add list
    offers them by kind, and each is drawn from the document's own data, which is this server's, so the console's
    policy admits it; the scene keeps it as a `CSV` or `GeoJSON` layer, styled, placed and filtered as a feature layer.
    A file elsewhere on the web is not offered: the policy names no other origin.
12. **A point cloud's look** — Scene Viewer's point cloud renderers: true colour (RGB), by elevation (a stretch over
    the layer's height range), by LAS class (ground, vegetation, building, water, unassigned, noise) and by intensity,
    each offered when the layer has the attribute, kept as the layer's renderer. *2026-11-07: each of the four seen
    drawn on Esri's Morro Bay subset. The height range is the layer extent's z, or, when the extent has none (this
    sample's has none), the ELEVATION attribute's cached statistics; without them it was 0–100 m and most points were
    one colour.*
13. **Not built from the design review** (ADR-269 condition 4's reviewer, 2026-11-05): adopting the 2D map's icon rail for
   the panel's tabs. The six text tabs fit one row at 340 px; the rail is a consistency gain, not a defect.
   *(Deferred.)* Since §14 there are seven for a scene's owner, and the row scrolls at 340 px as it was built to.
   ***Superseded 2026-11-07 by [ADR-279](ADR-279-the-scene-viewer-takes-the-map-viewers-shape.md)**, after the owner called the
   interface very bad: the icon rail, and the map viewer's whole shape, were adopted.*
14. **Analysis from the scene** (2026-11-07, the comparison's §D) — Scene Viewer's *Analysis*: an *Analysis* tab, for
    whoever may change the scene and is signed in, runs the Spatial Analysis Service's tools on a hosted feature layer of
    the scene, or on the feature layer a scene layer made here is related to (§4's relationship). **The tools, their
    questions and what they send are analysis.js's**, the one copy Studio's map and an item's *Analyze…* dialog use
    (ADR-222 condition 7), so a third copy is not made (D-130). The result, a hosted feature layer of the reader's, is
    added to the scene. A run left by leaving the tab goes on, and its end is said in the header.
    *(INFERRED, as §5.1–5.9: that Scene Viewer's analysis is in the sentence. Scene Viewer's 3D-only analyses — viewshed
    and volume — are not the service's and wait on the SDK, as the comparison lists.)*
15. **Colour by a field's values, or by classes** (2026-11-07, the comparison's §A) — two of Scene Viewer's smart mapping
    styles beside one colour and a ramp: *a colour for each value* (a unique-value renderer over the ten most common
    values of a text or whole-number field, the rest grey as *Other*) and *classes of a number field* (a class-breaks
    renderer, five natural-breaks classes from light to the chosen colour). Both are computed by the SDK's own statistics
    (`uniqueValues`, `classBreaks`) and keep the shape, size and height of the simple look, so changing the colouring
    changes nothing else. The form reads both back from a renderer it wrote. *(INFERRED, as §5.1: ten values and five
    classes are this page's defaults, not ArcGIS's, which lets a reader change both; the web style gallery is not built.)*
16. **Play captions each slide** (2026-11-07, the comparison's §B) — Scene Viewer's *Present*: while the slides play, the
    slide's title and description and where it is in the presentation (*2 of 5*) are shown over the scene, in the view's
    own corner, and announced as a status. Cover and caption *styles* — a choice of how they look — are not built.
17. **A voxel package is served as ArcGIS serves a voxel layer** (2026-11-07, the comparison's §G). Measured on a
    voxel package ArcGIS Pro wrote (NASA's `AIRS_Voxel`, public on ArcGIS Online, used locally and not committed) and
    on an ArcGIS Online hosted voxel layer: the layer document is `layer.json`, not `3dSceneLayer.json`. The service
    document says `layerType: Voxel` and lists no layers. The layer document is at `SceneServer/layer`, and the
    resources (`variables/…`) are below the service itself. Served so, the Maps SDK loads it as a voxel layer; served
    as a 3D object service, it refused it. **Drawing it is not achieved.** SDK 4.29 created no layer view for this
    WGS 84 sample in a global scene or in this viewer's local scene, which is Web Mercator. A projected voxel package
    and a local scene in its own system (§A's *no other projected system*) are what to try next. A layer the scene
    cannot draw now says why when it is added, instead of drawing nothing.
    *(Drawn 2026-11-07: a voxel package in Web Mercator, a public sample measured locally and not committed, was
    drawn whole in this viewer's local scene. The rule the SDK keeps is the layer's own coordinate system: a WGS 84
    voxel is drawn in neither kind of scene, and one in another projected system needs a local scene in that system,
    which this viewer does not make (§A's *no other projected system*). The added-layer message now says so.)*
18. **Volume, viewshed and dimensions** (2026-11-07, on SDK 4.34, ADR-273) — Scene Viewer's three tools that 4.29 did
    not have. In 4.34 they are analyses with no widget (`VolumeMeasurementAnalysis`, `ViewshedAnalysis`,
    `DimensionAnalysis`), so each is a small panel of this page's. The panel holds a sentence saying what the tool does,
    *Start* (the SDK's own interactive placement), *Done*, *Clear*, and for volume the cut, fill and net. The analysis
    is in the view while its tool is open and is taken out when it closes, as a widget's measurement is. Slides that
    keep their analyses, legend and pop-up (4.34's `Slide.elements`), and the caption and cover layouts
    (`Slide.layout`), are now possible and not yet built. *(Built the same morning, §19.)*
19. **A slide keeps its slice, legend and pop-up, and is played as a caption, a cover or the scene alone** (SDK 4.34's
    `Slide.elements` and `Slide.layout`). Capturing or updating a slide records the open slice, whether the legend is
    shown, and the open pop-up, by layer and object id. Going to the slide puts them back: the slice is added, the
    legend tab is shown, and the features are queried and their pop-up opened. Each slide is *Shown as* caption, cover
    or scene only, and Play draws it so. *Tested: the cover layout and the legend, saved and read back. The slice and
    the pop-up go through the same code and are not yet tested by a console test.*
20. **A local scene in a layer's own projected system** (2026-11-07, the comparison's §A *no other projected system*).
    A 3D layer (scene, building, point cloud, mesh, voxel) in a projected system other than the local scene's is
    drawn only in a local scene in its own. Added to a new, empty local scene, it offers *Start a local scene in
    EPSG:n*, which opens the viewer as `?mode=local&wkid=n` with the layer. That scene has no basemap, since every
    basemap this server offers is Web Mercator, and the offer says so. To a scene with layers, it says why it was not
    added. *Measured: a public voxel sample in EPSG:2056 (Swiss LV95), used locally and not committed, was offered, the
    scene started in 2056 and the voxel drawn.*

## 6. Consequences

**Positive.** A scene can be authored here as in Scene Viewer, and what is authored is the web scene document Pro and
the SDK read.

**Negative.** The look editor is narrower than Scene Viewer's and replaces a richer renderer once used. The SDK's
widgets are styled by the SDK. More of the SDK is fetched, on first use.

**Ports created.** None: the viewer is a page of the console, and the SDK is ADR-253's dependency.

**State.** A scene layer's source as an item resource, and one relationship type more (migration 133). Everything a
viewer authors lives in the web scene item ADR-269 keeps.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | What the SDK's `WebScene.toJSON` writes for a renderer, a pop-up, slides and the ground is what Pro reads | Unmeasured — condition 2 |

## 8. Dependencies

**Depends on:** ADR-269, ADR-253, ADR-184 (basemap gallery), ADR-182 (elevation).

**Depended on by:** —

## 9. Conditions

1. **The scene viewer passes the ux-designer review**, first-run state and phone included — the same condition as
   ADR-269's fourth, for the screens this ADR adds. *(Open. 2026-11-05: the first review found a blocker — a look not
   yet applied was lost when the list redrew — and sixteen more; all but the icon rail were applied. 2026-11-06: a
   confirming review said **not pass** and found a second blocker the first had not — the tab row, a flex child with
   `overflow-x`, shrank to nothing whenever the pane was taller than the screen, the first run included — and the phone's
   open tool list running past the scene; both fixed, with a test that the row stays 30 px tall and a reader test on a
   fresh page as a real second member.)* *(Discharged 2026-11-06: a third look at the fixed screens — first run, a
   layer's settings, slides, the phone with its tools open, and a real second member's fresh page — passed, with two
   minors applied (Save bright only when there is something to save; Delete set apart on a slide's row). The icon rail
   stays deferred, §5.11.)*
2. **A web scene authored here — a styled layer, a pop-up, slides — opens in Pro as it was authored.** Owed by the owner:
   it needs the desktop.
3. **The parity with ArcGIS Online and Enterprise is written down**, item by item, with what is built, what is not and
   why, in [3d-parity-arcgis.md](../reviews/3d-parity-arcgis.md). *(Discharged 2026-11-06: written from ArcGIS Online's
   pages and Enterprise 11.4's Scene Viewer page, every row cited; the gaps it found are each built, owed, or listed for
   the owner with a recommendation.)*

4. **The second night's screens pass the ux-designer review** — *Only within the current view*, the Analysis tab, the
   colouring controls and Play's caption (§5.14–5.16, ADR-271). *(Open. 2026-11-07: the first review found no blocker
   and six majors — the seventh tab out of sight at 340 px, two controls both called *Colour*, a view that cannot be a
   box filtering silently, the caption's live region, no Stop on the scene, the SDK's developer text in an error — all
   applied, with most of the minors; a confirming review is owed.)* *(Deferred 2026-11-07 to ADR-279 condition 1: the screens it reviewed were redrawn in the map viewer's shape, so the
   confirming review is of the new shape.)*

## 10. Revisit triggers

- A user who needs unique-value or class-break styles, or 3D models from a web style, in a scene.
- An SDK version that adds viewshed or volume measurement.

## 11. Dissent

None recorded.
