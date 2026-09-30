# ADR-110 — A web map sets its layers' pop-ups

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — stored where the ArcGIS Web Map specification keeps it and drawn by the viewer that reads it; not measured is what an ArcGIS client draws from a `popupInfo` saved here |
| **Decided** | 2026-10-01, by owner decision (*"2 devam sırayla"*, the last item of the ArcGIS reviewer's list) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-079](ADR-079-a-web-map-is-a-saved-document.md) (the document carries a layer's pop-up), beside [ADR-104](ADR-104-a-web-map-styles-its-own-layers.md) (its style) |

---

## 1. Context

The ArcGIS reviewer: *"No pop-up or label configuration anywhere in Studio. Field Maps and Dashboards users configure
pop-ups first."* A click on the Map Viewer listed every attribute of every feature under its column name, and nothing
chose which, in what order, under what label, or with what title.

## 2. Alternatives considered

### Alternative A — The map's own pop-up, as `popupInfo` in the Web Map document (chosen)

**Argument for.** The Web Map specification's operational layer has `popupInfo` (a `title` with `{field}`
substitution and `fieldInfos` with `fieldName`, `label`, `visible`) and `popupEnabled`; ArcGIS clients read them
from a map. It is ADR-104's shape for pop-ups: the map decides for itself, and the map's own saves it.

### Alternative B — A layer default pop-up, stored with the layer

**Argument against, for now.** Portal also keeps a pop-up on the layer's item, for every map that has not set its
own. That needs somewhere on the server to keep it and every face to read it; the map's own is the first half and
stands on its own.

## 3. Counterarguments to the preferred option

- *Labels are a separate item of the reviewer's sentence.* They are: map labels are drawn by the tile style and the
  server renderer (ADR-100), not by a map's pop-up, and are not in this decision.
- *Only a title and fields.* Portal's pop-ups also hold text blocks, media, charts and Arcade expressions; this is
  the plain form that every ArcGIS client draws the same way.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| `popupInfo.title`, `fieldInfos[].fieldName/label/visible` and `popupEnabled` are the Web Map's | ArcGIS Web Map specification | publicly documented |

## 5. Decision

5.1 Each feature layer in the Map Viewer has a *Pop-up* panel: shown or not, a title with `{field}`, and which of the
layer's fields appear under what label. *Apply to this map* stores `popupInfo` / `popupEnabled` on the layer in the
document; *Every field again* removes it.

5.2 A click draws the map's pop-up where the layer has one — the title substituted, the visible fields in their order
under their labels — and every attribute as before where it has none. A layer whose pop-up is off is not asked.

## 6. Consequences

**Positive.** A map shows what its author chose on a click, and an ArcGIS client opening it reads the same choice.

**Negative.** No layer default pop-up; no text, media or expressions.

**State.** In the web map's stored document; nothing in the catalogue.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ArcGIS clients draw a `popupInfo` saved here as this viewer does | `INFERRED` |

## 8. Dependencies

**Depends on:** ADR-079, ADR-104.

**Depended on by:** —

## 9. Revisit triggers

- A layer default pop-up is asked for (Alternative B).

## 10. Conditions

1. **The panel stores the pop-up and a click draws from it** — **DISCHARGED 2026-10-01**,
   `WebMapViewerTests.A_layers_pop_up_is_set_in_the_map_and_drawn_from_it`.
2. **An ArcGIS client opens a map saved here and shows the same pop-up** — not yet.
