# ADR-132 — The Map Viewer has a time slider

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the server already answers `time`; the slider is ArcGIS Map Viewer's shape over it |
| **Decided** | 2026-10-01, under the owner's standing instruction to make Studio usable as Portal is |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-079](ADR-079-a-web-map-is-a-saved-document.md) (what the Map Viewer offers, and that a map keeps its time window) |

---

## 1. Context

A layer's time field is set in Studio and the server writes `timeInfo` and answers `query?time=` — but the Map Viewer
used neither, so a time-enabled layer drew every feature at once. The ArcGIS reviewer's third pass listed the missing
time slider among the authoring gaps.

## 2. Alternatives considered

### Alternative A — A window over the map's time span, sent as `time` to every layer with time (chosen)

Shown when a layer on the map has `timeInfo`, over the span its layers cover, cut into time stops of a unit that
suits it (minutes to years, at most about two hundred stops) so a key press moves a meaningful step; one track with a
start and an end that do not cross, step back and forward, Play walking the window forward (a twentieth of the span
when started from all of it), Show all time to clear it. An empty answer names the window rather than the view, the
layer's row says it is narrowed, and a moved window closes the open card. Every request a time-enabled layer makes — drawing,
identify, the table — carries `time=start,end`. The window is kept in the Web Map's `widgets.timeSlider`, where ArcGIS
keeps it.

### Alternative B — A date filter in the layer's filter box

**Against:** a `where` on a date column is per layer and typed; the slider is the map's, across its layers, and is
what an ArcGIS author expects to find.

## 3. Counterarguments to the preferred option

- HTML has no two-thumb range: two labelled range inputs lie on one track with only their thumbs taking the pointer,
  and the window between them is drawn on the track — keyboard-operable as two inputs, read as one window.
- At phone width with the table open the map is too short for the bar, which hides; the layer's row still says the
  window applies.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A time-enabled layer brings the slider; moving it asks the layer for that time; the window is kept in the document | `WebMapViewerTests.A_layer_with_time_gets_a_time_slider_that_filters_what_is_drawn` | this repository |

## 5. Decision

The Map Viewer shows a time window when a layer has time, applies it to every request such a layer makes, and keeps
it with the map.

## 6. Consequences

**Positive.** Time-enabled data is explored as time; ArcGIS clients read the saved window.

**Negative.** None known.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Studio's parity with Portal is the owner's standing direction | Stated by the owner |

## 8. Dependencies

**Depends on:** ADR-079; the layer time field already served.

**Depended on by:** —

## 9. Revisit triggers

- A layer with an end-time field, which this window treats as instants.
