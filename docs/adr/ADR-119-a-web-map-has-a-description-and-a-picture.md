# ADR-119 — A web map has a description and a picture

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — two columns and the Map Viewer's own canvas |
| **Decided** | 2026-10-01, by owner decision (*"Sırayla git"*, the ArcGIS reviewer's second-pass item 8) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The reviewer: *"The web map item page has title, summary, Share, Delete and Change owner. It has no thumbnail, no
description."* Portal's web map item carries both: a description beside the one-line summary, and a thumbnail the
Map Viewer takes of the map when it is saved. Pro and the Python API read them from the item.

## 2. Alternatives considered

### Alternative A — The viewer's own canvas, sent on save (chosen)

**Argument for.** The map is already drawn in the browser exactly as its author arranged it; the server would have to
draw every layer and basemap again to get the same picture. Portal's Map Viewer does the same.

### Alternative B — The server draws the map

**Argument against.** It means rendering each layer kind server-side, a basemap included, for a picture the browser
already holds.

## 3. Counterarguments to the preferred option

- *A basemap from another origin without CORS taints the canvas*, and the browser refuses to export it. The map is
  saved either way and keeps the picture it had; nothing is said, because nothing went wrong with the save.
- *A picture is only as current as the last save in the viewer*, as in Portal.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Portal items carry `description` and `thumbnail`, served from `info/thumbnail/…` | ArcGIS REST *Item*, *Item Thumbnail* | publicly documented |
| The description and picture are kept, a non-PNG is refused, and the portal face says and serves both | `WebMapPictureConformanceTests` | this repository |

## 5. Decision

5.1 Migration 74: `web_map.description` and `web_map.thumbnail` (the PNG as sent). `PUT /content/webmaps/{id}/description`
and `PUT /content/webmaps/{id}/thumbnail` (a PNG of at most 1 MB) by whoever may change the map;
`GET /content/webmaps/{id}/thumbnail` to whoever may open it.

5.2 The map's answer and its portal item say the description and where the picture is; the portal face serves it
from `info/thumbnail/thumbnail.png`. An item with no description says its summary there, as before.

5.3 The Map Viewer sends a 600 × 400 picture of the view after each save. Studio's item page shows the picture, the
summary and the description, which is edited with the title; My content shows the picture in the map's row.

## 6. Consequences

**Positive.** A map is recognisable in lists, here and in ArcGIS clients.

**Negative.** No uploaded picture of one's own; no rich text.

**State.** Two columns.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A picture taken on save is what an author expects | `INFERRED` — Portal's behaviour |

## 8. Dependencies

**Depends on:** ADR-079 (web maps), ADR-102 (the item page).

**Depended on by:** —

## 9. Revisit triggers

- Somebody wants to upload a picture of their own.

## 10. Conditions

1. **The description and picture are kept and said, here and on the portal face** — **DISCHARGED 2026-10-01**,
   `WebMapPictureConformanceTests`.
2. **Studio edits the description and the viewer sends the picture** — **DISCHARGED 2026-10-01**,
   `WebMapViewerTests.A_web_map_has_an_item_page_with_its_layers_and_its_sharing`.
