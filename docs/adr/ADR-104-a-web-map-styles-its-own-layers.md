# ADR-104 — A web map styles its own layers

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the storage is the ArcGIS Web Map specification's own place for it and the renderers are the ones `generateRenderer` already builds; what is not measured is whether ArcGIS clients opening a map saved here draw its layers the same way |
| **Decided** | 2026-10-01, by owner decision |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-079](ADR-079-a-web-map-is-a-saved-document.md) (the map document carries a layer's style); [ADR-102](ADR-102-the-studio-item-is-one-page-and-every-setting-has-one-home.md) §5.4 (the layer default keeps its home; a map's own style is a second, narrower one) |


> **2026-10-01: [ADR-110](ADR-110-a-web-map-sets-its-layers-pop-ups.md) adds the same for pop-ups** — `popupInfo` and `popupEnabled` beside `layerDefinition.drawingInfo`, from a *Pop-up* panel beside *Style*.
---

## 1. Context

The owner asked where a layer's symbology changes, and whether the Map Viewer changes it. It did not: the Map
Viewer (`webmap.js`) read a layer's renderer and drew it, and nothing else. A layer's appearance changed in one
place, the item's Visualization › Style, and that changed it for every map and every client.

Portal has two levels. The item's Visualization sets the **layer's default**; the Map Viewer's *Styles* sets a
layer's look **in that map only**, saved in the web map, so one layer can be red in one map and blue in another.
The owner said on 2026-09-30 that *"ArcGIS symbology'i web map'e yıktı"*, and asked for this level on 2026-10-01
(*"Evet"*).

## 2. Alternatives considered

### Alternative A — The map's own style in the Web Map document, built from `generateRenderer` (chosen)

**Argument for.** The ArcGIS Web Map specification already has the place: an operational layer's
`layerDefinition.drawingInfo`. A map saved here carries it where ArcGIS clients look for it, and the viewer's
existing renderer reader draws it with no new drawing code. Unique values and class breaks come from this
server's `generateRenderer`, which the console's editor already uses — one classifier.

**Argument against.** It is Portal's *Styles* panel in its simple form — single symbol, unique values, counts
and amounts — not the whole editor with symbol stacks, pictures and CIM. A map that needs more uses the layer's
default.

### Alternative B — Embed the Studio editor in the Map Viewer

**Argument for.** Every capability the editor has, in the map.

**Argument against.** The editor writes CIM for a layer's default and is three columns wide; a map viewer's
panel is 340 pixels, and the Web Map stores `drawingInfo`, not CIM. It would be a second copy of the largest
screen in the product, fitted into the smallest.

## 3. Counterarguments to the preferred option

- *Two places to style a layer is the "symbology everywhere" the owner objected to.* It is two levels, not two
  doors to one setting: the map's style changes that map; the layer's default changes every map that has not
  styled it. The panel says which one it changes, and *Save as the layer's default* is offered only to someone
  who may change the default — the same door ADR-102 already has, reached from the map.
- *A map's style goes stale when the layer's schema changes.* A unique-value renderer on a column that was
  dropped draws its default symbol; the viewer says the layer's own style can be put back with one press.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The Web Map places a layer's renderer at `operationalLayers[].layerDefinition.drawingInfo` | ArcGIS Web Map specification | publicly documented |
| `PUT /admin/layers/{name}/symbology` accepts an Esri `drawingInfo` | `SymbologyConversion.Read` tells formats apart by `renderer` | src/Graticula.Core/Cartography/SymbologyConversion.cs |
| `generateRenderer` builds unique-value and class-breaks renderers with a colour ramp | `GenerateRendererEndpoints` | src/Graticula.Host/GenerateRendererEndpoints.cs |

## 5. Decision

5.1 **A feature layer on a map may carry its own style**, stored as `layerDefinition.drawingInfo` in the map's
Web Map document. The viewer draws it in place of the layer's default; removing it returns the layer to its
default.

5.2 **The Map Viewer's layer list has a *Style* control** per feature layer: *Single symbol* (colour, size or
width), *Unique values* (a field), *Counts and amounts* (a numeric field, a number of classes, a method and a
ramp), and *Layer's own style*. Unique values and counts come from the layer's `generateRenderer`.

5.3 **Save as the layer's default** is offered in the same panel to a user whose role may publish, and sends the
same `drawingInfo` to `PUT /admin/layers/{name}/symbology`; the server's ownership rule (ADR-075) decides. After
it, the map's own copy is removed, so the map follows the default it just set.

5.4 **Vector-tile and map-image layers are not styled here**: their appearance is the service's tile style and
the server's drawing, which are set in the item.

## 6. Consequences

**Positive.** One layer can look different in different maps, as in Portal, and ArcGIS clients opening the map
see the same `drawingInfo`. The layer's default has one home still, reachable from the map by whoever owns it.

**Negative.** Only the simple renderers. A map's style does not follow a later change to the layer's default —
by design, as in Portal.

**State.** The map's style is in the web map's stored document; nothing new in the catalogue, nothing at runtime.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ArcGIS clients read `layerDefinition.drawingInfo` from a map saved here | `INFERRED` |

## 8. Dependencies

**Depends on:** ADR-079 (web maps), ADR-052 (`generateRenderer`, the canonical document), ADR-075 (who may
change a layer's default).

**Depended on by:** —

## 9. Revisit triggers

- A map needs a renderer the panel cannot make — consider opening the item's editor from the map.
- An ArcGIS client is found to ignore a map's `drawingInfo`.

## 10. Conditions

1. **The viewer draws a map's own style and saves it in the document**, and *Layer's own style* removes it —
   tested in the web map viewer suite. **DISCHARGED 2026-10-01** — `WebMapViewerTests.A_layer_is_styled_in_the_map_and_can_become_its_default`
   applies a style, finds it in the document and in the saved body; *The layer's own style* is the same Apply path
   with the renderer removed.
2. **Save as the layer's default sends the `drawingInfo` to the symbology endpoint** and removes the map's copy —
   tested. **DISCHARGED 2026-10-01** — the same test: the `PUT …/symbology` is sent and the map's copy goes.
3. **An ArcGIS client opens a map saved here and draws its styled layer the same way** — not yet measured.
