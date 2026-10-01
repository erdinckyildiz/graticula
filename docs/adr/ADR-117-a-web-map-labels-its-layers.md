# ADR-117 — A web map labels its layers

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — Web Map's own fields, drawn by the viewer that already draws the layer |
| **Decided** | 2026-10-01, by owner decision (*"Sırayla git"*, the ArcGIS reviewer's second-pass item 7) |
| **Supersedes** | — |
| **Superseded by** | — |


> **Amended 2026-10-01 by [ADR-131](ADR-131-labels-have-a-visible-range.md).** A label class carries the scales it shows between, set in the Labels panel, where `minScale` and `maxScale` were written as zero and never read.

---

## 1. Context

The ArcGIS reviewer: *"There are no labels (`labelingInfo = null`)."* Labelling a layer by a field is among the first
things done to a map in ArcGIS Map Viewer, and Pro and Field Maps draw a map's labels from the Web Map document —
`showLabels` on the operational layer and `layerDefinition.drawingInfo.labelingInfo`.

## 2. Alternatives considered

### Alternative A — Labels in the map, written where ArcGIS clients read them (chosen)

**Argument for.** The same shape as ADR-104's styles and ADR-110's pop-ups: a panel in the Map Viewer writes the Web
Map's own fields, the viewer draws them, and every ArcGIS client that opens the map reads them.

### Alternative B — A layer default served in the layer document's `labelingInfo`

**Argument against, for now.** It is the next step, as *Save as layer default* was for styles; maps are where labels
are set first.

## 3. Counterarguments to the preferred option

- *Only a field's value.* Arcade expressions, label classes and scale ranges are not drawn. A map whose label is an
  expression this viewer cannot run draws without it and says so in the panel; applying replaces it.
- *A layer with no style of its own in the map gets the service's renderer copied beside its labels*, because a
  client that takes `drawingInfo` whole would otherwise lose its symbols.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Web Map `showLabels` and `labelingInfo` with `labelExpressionInfo` and an `esriTS` symbol | ArcGIS Web Map Specification | publicly documented |
| The panel writes them, the viewer draws the field's value over the symbol, and Remove takes them out | `WebMapViewerTests.A_layer_is_labelled_in_the_map_and_drawn_from_it` | this repository |

## 5. Decision

5.1 Map Viewer's feature layers have a *Labels* panel: show or not, the field, size, colour and a white halo.
*Apply to this map* writes `showLabels` and one `labelingInfo` class — `$feature["field"]` and the older `[field]`,
the placement ArcGIS uses for the layer's geometry, an `esriTS` symbol — and redraws the layer.

5.2 The viewer draws a label it can read (one field's value, in either spelling) over the feature's own symbol,
decluttered.

## 6. Consequences

**Positive.** Maps carry labels to Pro and Field Maps.

**Negative.** No expressions, classes or label scale ranges; no layer default.

**State.** None — the Web Map document.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ArcGIS clients draw a Web Map label class with both `labelExpressionInfo` and `labelExpression` | `INFERRED` — documented, not run against Pro here |

## 8. Dependencies

**Depends on:** ADR-104 (styles in the map), ADR-110 (pop-ups in the map).

**Depended on by:** —

## 9. Revisit triggers

- A layer default is wanted, or an Arcade label.

## 10. Conditions

1. **The panel writes the labels and the viewer draws them** — **DISCHARGED 2026-10-01**, `WebMapViewerTests`.
