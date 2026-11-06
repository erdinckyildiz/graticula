# ADR-279 — The scene viewer takes Scene Viewer's and Studio's map's shape

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-11-07. **Prompted by the owner**: *"Graticula scene arayüzü gerçekten de çok kötü bu arada"* ("the Graticula scene interface is really very bad, by the way"). The shape is this ADR's, and it is the one Studio's map already has by [ADR-259](ADR-259-the-map-viewer-takes-arcgis-map-viewers-shape.md). |
| **Supersedes** | ADR-270 §5.13's deferral of the icon rail; the scene viewer's tab row and its floating tool list. |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

The scene viewer grew a feature at a time
([ADR-269](ADR-269-scenes-web-scenes-scene-packages-and-generated-i3s.md), [ADR-270](ADR-270-scene-viewer-to-arcgis-enterprise-parity.md)).
Screenshots taken on 2026-11-07, beside Studio's map, showed what the owner meant:

- the scene's title field sat inside the panel;
- seven text tabs overflowed a 340 px row, and *Legend* was cut;
- a layer's settings opened as one long form inside the layer list;
- sixteen tools were a plain text list floating over the scene, with each tool's widget floating beside it.

Studio's map, by ADR-259, has ArcGIS Map Viewer's shape: a left toolbar of icons with its panel, and a right toolbar
with the selected layer's settings and the map's tools. The two viewers looked like two products.

## 2. Alternatives considered

### Alternative A — the map viewer's shape, with Scene Viewer's grouping of 3D tools (chosen)

- **Left toolbar**: Add, Layers, Basemap, Ground, Slides, Legend, Scene (the scene's properties: title, type, embed)
  and, for its owner, Analysis. Each opens the left panel with its name as heading. The open tool's button closes the
  panel, giving the scene the room.
- **Right toolbar**: the selected layer's *Properties*, *Styles*, *Pop-ups* and *Visibility*, then the scene's tools in
  Scene Viewer's groups: *Measure* (distance, area, volume, dimensions), *3D analysis* (line of sight, slice, shadows,
  elevation profile, viewshed), *Daylight* (daylight, weather), *Search*, *Location*, *Edit*, *Building* and *Time*.
  A group the scene cannot use is not shown. A group opens the right panel with its tools to choose from, and the first
  (or the last used) open. A tool's widget is drawn in that panel, not over the scene.
- **A layer's settings** open in the right panel, from its row's *Settings* or the toolbar, with the layer and its part
  chosen at the top.
- **A phone**: ADR-259's. The left toolbar runs along the bottom and its panel is a sheet. The right toolbar's icons sit
  at the scene's top right, with one button for a layer's settings, and its panel is a sheet too.

### Alternative B — keep the tabs and tidy them

**Rejected.** The faults were structural: one column for everything, and tools over the scene.

## 3. Counterarguments to the preferred option

- **Ids and behaviour the tests and other pages rely on were kept** where they still meant the same thing: the tabs
  and panes, `#toolPanel`, `[data-detail]`, the layer list's `data-act`s. What changed in the tests is how a tool is
  reached: its group's button, then the tool.

## 4. Evidence

- Screenshots before and after, desktop and phone (2026-11-07, scratchpad; not committed).
- The scene console classes (`SceneViewerTests`, `SceneViewerToolsTests`, `SceneLayerSourceTests`, `TilesArchiveTests`)
  were run against the new shape, with the tool helper `OpenToolAsync` reaching a tool as a reader does.

## 5. Decision

As Alternative A. The embed view (ADR-275) keeps the right toolbar's tools and drops the left toolbar, the layer
settings and everything that changes the scene.

## 6. Consequences

- The scene viewer and Studio's map are one product to look at.
- **State.** None.

## 7. Assumptions this decision rests on

- Scene Viewer's grouping is what a Scene Viewer user expects. `INFERRED`, from its documentation's tool list
  ([3d-parity-arcgis.md](../reviews/3d-parity-arcgis.md) source 1).

## 8. Dependencies

ADR-259, ADR-269, ADR-270, ADR-275.

## 9. Conditions

1. **The new shape passes the ux-designer review**, desktop and phone, the first run and a reader's view. This
   replaces ADR-270 condition 4. *(Open. 2026-11-07: the first review of the new shape said it fixed the structural
   fault, found two blockers and ten majors, and all were applied the same hour:
   - the layer row is the map's, where the name selects and ⋯ holds Zoom to, Properties, Move and Remove (clicking the
     name used to hide the layer);
   - the phone's bottom bar holds five, with Ground, Legend and Scene behind *More*;
   - the right toolbar stops above an open sheet;
   - Escape hides the panel and keeps the tool and its measurement;
   - Remove offers Undo, and the panel no longer moves on to another layer;
   - the server's Analysis is on the right toolbar, as in the map;
   - below 1600 px, opening the right panel closes the left;
   - a tool's panel says it is opening, and a group's tools are a choice of one;
   - a closed panel's tab no longer looks open;
   - the layer's properties put Remove last, apart from the rest, and fold its source;
   - the first run says where to start, and the layer buttons are hidden until there is a layer;
   - names and order follow the map (Properties, Styles, Filter, Pop-ups; *Light & weather*);
   - the phone sheets look like the map's;
   - pills and buttons have edges you can see;
   - the toolbar's orientation is right on a phone, and focus stays on the button last clicked.
   *(The confirming review said NOT PASS, and close. It found two majors and eight minors, all applied the same hour:
   - a reader sees *Details* only;
   - the pills are the phone's alone;
   - no stray separator;
   - one header sentence, and no repeated first-run hint;
   - the analysis pane's markup;
   - the SDK widgets in Studio's accent;
   - no phone rail scrollbar;
   - shorter buttons on short screens;
   - the status on the title's line on a phone.
   A third look is owed.)*
