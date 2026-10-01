# ADR-133 — The Map Viewer prints the view as a page

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — composed in the browser from what the map already drew; no print service |
| **Decided** | 2026-10-01, under the owner's standing instruction to make Studio usable as Portal is |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-079](ADR-079-a-web-map-is-a-saved-document.md) (what the Map Viewer offers) |

---

## 1. Context

The ArcGIS reviewer's third pass listed printing among the Map Viewer's authoring gaps. A Portal author hands a map
over on paper or as a PDF as often as by link: ArcGIS Map Viewer's Print makes a page with a title, a legend, the
scale and credits.

## 2. Alternatives considered

### Alternative A — Compose the page in the browser (chosen)

The area in view drawn again at the page's own pixels — A4 at 150 dpi, the map set to that size for one render and put
back, as OpenLayers' export example does — then composed with the map thumbnail's composition onto the page: a title, a
legend of what is drawn in view class by class, a scale bar and the paper's scale from the ground a page pixel covers
at the centre, a north arrow, the date, the time window when there is one, and the credits. Downloaded as a PNG, or
opened as a page the browser prints or saves as PDF.

The design review measured the first version — the screen's picture stretched to the page — printing 1:31,333 for a
map at about 1:14,800, blurred at phone width; the second pass measured the scale bar, the printed scale and the
geodesic ground distance agreeing to within a tenth of a percent.

### Alternative B — A server-side print service, as ArcGIS's `ExportWebMap`

**For:** what ArcGIS clients call, and vector output. **Against:** a renderer of every layer kind on the server, a
second drawing of what the browser has already drawn; [build-vs-adopt](../build-vs-adopt-policy.md) Tier 1 would make
it ours to write. Revisit if a client asks the server to print.

## 3. Counterarguments to the preferred option

- A raster page at 150 dpi, not a vector PDF.
- *1:n on A4* holds when the page is printed at 100%; the print page asks for no margins, and a reader can still choose
  to fit it to another paper.
- The browser's print dialog is the PDF writer; a pop-up blocker can stop the print page, which is said, and PNG is
  the way round it.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The page is composed as an A4 sheet with the map's title; Download PNG makes it a file | `WebMapViewerTests.A_layers_table_opens_under_the_map_and_a_bookmark_is_kept_with_it` | this repository |

## 5. Decision

The Map Viewer's Print tab composes the view as an A4 page and downloads it or opens it to print.

## 6. Consequences

**Positive.** A map leaves the screen as a handout or a PDF.

**Negative.** Raster only.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Studio's parity with Portal is the owner's standing direction | Stated by the owner |

## 8. Dependencies

**Depends on:** ADR-079, ADR-119 (the composition the thumbnail uses).

**Depended on by:** —

## 9. Revisit triggers

- An ArcGIS client is measured calling a print service on this server.
