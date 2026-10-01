# ADR-121 — A point layer is drawn as a heat map, or sized by a number

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — Web Map's own renderer shapes, drawn by OpenLayers' own layers |
| **Decided** | 2026-10-01, by owner decision (*"devam"*, the ArcGIS reviewer's second-pass item 7) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The reviewer: *"Style is simple, unique values or class breaks … no heatmap and no size variables."* ArcGIS Map
Viewer's *Heat map* and *Counts and Amounts (size)* are two of the first styles chosen for a layer of points —
incidents, inspections, sales — and Pro and Field Maps draw them from the Web Map: a `heatmap` renderer, and a
`sizeInfo` visual variable on a simple renderer.

## 2. Alternatives considered

### Alternative A — In the map, as ADR-104's styles are (chosen)

**Argument for.** The Style panel already writes the map's `drawingInfo`; these are two more shapes of it, and
OpenLayers draws both — its Heatmap layer for one, a symbol per size for the other.

### Alternative B — Also as the layer's default

**Argument against.** The server draws a layer's default for its tiles and thumbnails, and its renderer does not
draw either shape; *Make default everywhere* is not offered for them.

## 3. Counterarguments to the preferred option

- *A heat map is not clicked for one feature.* The panel says so; a click still identifies what is under it.
- *The size range is the layer's at the moment of styling*, from its statistics, as ArcGIS Map Viewer fixes it.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| `heatmap` renderer (`blurRadius`, `colorStops`, `field`) and `sizeInfo` visual variable | ArcGIS Web Map Specification | publicly documented |
| Both are written, drawn, and a larger value draws a larger symbol | `WebMapViewerTests.A_point_layer_is_drawn_as_a_heat_map_or_sized_by_a_number` | this repository |

## 5. Decision

5.1 A point layer's Style offers *Counts and amounts (size)* — a number field, a colour, the smallest and largest
size, the value range read from the layer's statistics — and *Heat map* — a radius and an optional weight field.

5.2 The viewer draws a `sizeInfo` simple renderer with one symbol per size and a `heatmap` renderer as an
OpenLayers Heatmap layer whose gradient samples the renderer's colour stops.

## 6. Consequences

**Positive.** Density and magnitude, the two questions a point layer is most often asked, are answered on the map.

**Negative.** Not a layer default; points only.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ArcGIS clients draw these renderers from the Web Map as written | `INFERRED` — the specification's shapes, not run against Pro here |

## 8. Dependencies

**Depends on:** ADR-104 (styles in the map).

**Depended on by:** —

## 9. Revisit triggers

- The server's renderer learns either shape, and they can become layer defaults.

## 10. Conditions

1. **Both are written and drawn** — **DISCHARGED 2026-10-01**, `WebMapViewerTests`.
