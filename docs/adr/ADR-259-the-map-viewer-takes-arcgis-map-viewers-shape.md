# ADR-259 — The Map Viewer takes ArcGIS Map Viewer's shape

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — an owner decision on a described layout, not on a drawn one; condition 1 is the owner seeing it |
| **Decided** | 2026-11-03, by owner decision. Asked whether the viewer keeps the layout [ADR-079](ADR-079-a-web-map-is-a-saved-document.md) §5.4 took from NextGIS Web, takes two small repairs, or follows ArcGIS Map Viewer, the owner chose *ArcGIS düzeni* after saying of the earlier verdict *"mapviewer için de güzel dedim ama, alternatiflere açığım"*. Changing the drawing engine as well was offered and not chosen. |
| **Supersedes** | The layout sentence of ADR-079 §5.4 (the side panel with the tabs Layers, Search, Measure and Map), and ADR-079 §10a's first deferred recommendation, which this answers |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[ADR-255](ADR-255-the-interface-follows-arcgis-enterprises-structure.md) put every Studio, Server and System screen into
ArcGIS Enterprise's structure on 2026-11-02 and left the Map Viewer as it was, on the owner's word that *"Map Viewer tarafı
bizim daha güzel"*. On 2026-11-03 the owner reopened that: the viewer was liked, and alternatives are welcome.

The viewer had one rail of seven tools on the left — Layers, Search, Measure, Analysis, Bookmarks, Print, Map — and put
everything a single layer can be given (opacity, order, Style, Pop-up, Labels, Filter, Table, Add feature, Zoom to, Remove)
inside that layer's card in the Layers list, opening one editor at a time beneath it. A map of six layers with one editor
open was a long scroll in a 320-pixel column, and the controls a user looks for on the layer were next to every other
layer's.

ArcGIS Map Viewer, which the people this server is for already use, splits the same things in two: a left toolbar for what
the **map** has (add, layers, basemap, bookmarks, the map's own properties, print) and a right toolbar for what the
**selected layer** can be given (properties, styles, filter, pop-ups, labels), with the map tools (search, measurement,
analysis) under it. A layer in the list is a row: a visibility toggle, its name, and a ⋯ menu.

## 2. Alternatives considered

### Alternative A — Keep the layout, take ADR-079 §10a's two recommendations

A ⋯ menu on each layer card and a thumbnail on each map in the lists. Small, and the viewer would still look unlike the
rest of Studio after ADR-255. Not chosen.

### Alternative B — ArcGIS Map Viewer's layout on the same engine *(chosen)*

### Alternative C — B, and the drawing engine moved from OpenLayers to MapLibre GL

Smoother vector tiles and tilted views, at the cost of rewriting every tool the viewer has — measuring, editing, time,
printing, the table — on a second engine, with no measurement yet that the first is too slow. Not chosen; it stays open as
its own decision if a measurement ever asks for it (§3 of CLAUDE.md).

## 3. Decision

1. **Two toolbars.** The left one holds the map's tools: **Add**, **Layers**, **Basemap**, **Bookmarks**, **Print** and
   **Map** (the map's title, summary, who can open it, Save, Save as, Delete). The right one holds the selected layer's
   tools — **Properties** (opacity; for imagery how it is shown and whether a click reads its pixels), **Styles**,
   **Filter**, **Pop-ups**, **Labels** — and under a divider the map tools **Search**, **Measure** and **Analysis**. Each
   toolbar opens its own panel, beside it; a second press on the open tool closes it.
2. **A layer is a row** in Layers: the visibility checkbox, the layer's swatch, its name as the button that selects it,
   its kind and any state line it already had, and ⋯ with *Zoom to*, *Show table*, *Add feature* where the layer may be
   edited, *Move up*, *Move down* and *Remove*. Selecting a layer opens its Properties if no layer tool is open, and the
   open layer tool follows the selection. A layer tool with nothing selected selects the top layer.
3. **The basemap is its own tool**: the gallery's choice, moved from the Map form, and the basemap's own layers, moved
   from under the operational ones.
4. **Add is its own tool**: the list of services the viewer may read, which was a box that opened inside Layers.
   Layers keeps an *Add layer* button that opens it.
5. **On a phone** the left toolbar is the bottom bar it already was, the right one is a column of icon buttons at the
   map's right edge, and both open the same bottom sheet — one panel at a time.
6. **Nothing the viewer saves changes.** The Web Map document, its fields and every handler that writes them are the
   ones ADR-079 and its amendments describe; this moves where the controls are, not what they do.

## 4. Consequences

**Positive.** The viewer reads like the rest of Studio and like the tool its users come from; a layer's editor has a whole
column rather than the space under one card.

**Negative.** Every console test that reached a layer's editor through its card reaches it through the selection and the
right toolbar now, and a user of the old layout finds Search, Measure and Analysis on the other side.

**State.** None in the catalogue and none on the server. The page holds which tool is open and which layer is selected,
in memory, and forgets both when it closes.

## 5. Conditions

1. **The owner uses it** on dev before it reaches main, as with every interface change since 2026-10-17.
2. **Every screen passes the ux-designer review**, at 1,280 and 390 pixels, first-run state included — a new map with no
   layers, where the right toolbar's layer tools have nothing to act on. **DISCHARGED 2026-11-04**, on a re-check rather
   than on the first pass. Two ux-designer reviews drove the live page — 1,280 × 800, and 390 × 844 with touch
   emulation — signed in on an empty map and on one with a layer, and signed out on a public map; both returned
   *rework*, with no blocker and nine major findings between them. At 1,280: Show labels ticked on a layer with no
   labels, a Transparency slider whose label read the opacity once it moved, one Escape closing a pop-up and the panel
   under it, an open dimmed tool at 2:1, and a Measure hint promising what a click did not do. At 390: a reader's
   Properties offering four empty sheets, × dropping the keyboard on the page, the table opened under the sheet, and
   13 px fields 24–31 px tall. All were repaired on `night-1104`; a third review re-measured each, passed 1,280, and
   failed 390 on one item the repair had missed — Add's kinds buttons, 24 px on the first-run path, because
   `#addList .kinds button` outranked the phone's `button.tiny` rule — then passed 390 once that was fixed. axe-core
   4.10.2 found 0 violations in all 26 states run. **Left as minor, recorded rather than chased:** the inline *Add a
   layer* link (70 × 20, exempt as text), extra space above *Show labels* in the phone's Labels sheet, the map at 468 px
   with both desktop panels open, the time bar's wrap at that width, a stale coordinate readout, Remove without undo,
   and 11.5 against 12 px toolbar labels.

## 6. Dependencies

**Depends on:** ADR-079, ADR-255. **Depended on by:** —
