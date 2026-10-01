# ADR-131 — Labels have a visible range

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — the Web Map specification's own fields, read and written as ArcGIS does |
| **Decided** | 2026-10-01, under the owner's standing instruction to make Studio usable as Portal is |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-117](ADR-117-a-web-map-labels-its-layers.md) (what a label class carries) |

---

## 1. Context

ADR-117's labels wrote `minScale: 0, maxScale: 0` — every scale — and read neither back. The ArcGIS reviewer's third
pass, 2026-10-01, listed it among the Map Viewer's authoring gaps: a street layer's names over a whole country are a
grey smear, and an author sets labels to appear only once zoomed in. A map whose labels were given a range in ArcGIS
lost it when opened and saved here.

## 2. Alternatives considered

### Alternative A — Two choices of ArcGIS's named scales, written into the label class (chosen)

A *Visible range* of *Zoomed out to* (the most zoomed-out scale they show at, ArcGIS's `minScale`) and *Zoomed in
to* (`maxScale`), each a named scale or no limit, in a slider's order; the two cannot cross — a choice that would leave
no scale between them is not offered — and the map's current scale is shown beneath, with whether the labels show
there. The label layer is given the matching OpenLayers resolutions, converted as ArcGIS converts them.

### Alternative B — A slider over the scale range

**For:** ArcGIS's own control. **Against:** a slider of eleven named stops is the same choice harder to make by
keyboard; revisit if authors ask for scales between the named ones.

## 3. Counterarguments to the preferred option

- Named scales only: a range set elsewhere at another scale is kept and drawn, offered in the panel as *Custom* with its
  scale, so a change to anything else in the panel does not overwrite it.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A range is written as `minScale`/`maxScale` and the label layer is drawn only between them | `WebMapViewerTests.A_layer_is_labelled_in_the_map_and_drawn_from_it` | this repository |

## 5. Decision

A layer's Labels panel sets the scales its labels show between, kept in its label class.

## 6. Consequences

**Positive.** Labels can be kept off at scales where they only clutter; a range set in ArcGIS survives.

**Negative.** None known.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Studio's parity with Portal is the owner's standing direction | Stated by the owner |

## 8. Dependencies

**Depends on:** ADR-117.

**Depended on by:** —

## 9. Revisit triggers

- Authors ask for scales between the named ones.
