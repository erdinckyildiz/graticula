# ADR-118 — A web map's pop-up formats numbers and dates

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — one Web Map field per field info, drawn by the viewer that draws the pop-up |
| **Decided** | 2026-10-01, by owner decision (*"Sırayla git"*, the ArcGIS reviewer's second-pass item 7) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The reviewer: *"A pop-up is a title plus per-field visible/label … There is no number or date format."* A date
arrives from a FeatureServer as milliseconds and a measured value with every digit it was stored with; Portal's
pop-up configuration sets each field's decimal places, thousands separator and date format, in the Web Map's
`popupInfo.fieldInfos[].format`, which Pro and Field Maps read.

## 2. Alternatives considered

### Alternative A — The format column in the Pop-up panel (chosen)

**Argument for.** ADR-110's panel already lists the fields; a Format cell per field writes ArcGIS's own `format`
object (`places`, `digitSeparator`, `dateFormat`) and the viewer formats with it.

### Alternative B — Text and media elements, and Arcade

**Argument against, for now.** Each is a different content model; formats are the gap a reader meets first.

## 3. Counterarguments to the preferred option

- *Dates are shown in the viewer's own time zone*, as ArcGIS Map Viewer shows them.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| `fieldInfos[].format` with `places`, `digitSeparator` and `dateFormat` | ArcGIS Web Map Specification, *format* | publicly documented |
| A number and a date are drawn in the format given | `WebMapViewerTests.A_layers_pop_up_is_set_in_the_map_and_drawn_from_it` | this repository |

## 5. Decision

5.1 The Pop-up panel's fields table has a Format column: decimal places and *1,000s* for a number, one of ArcGIS's
date formats for a date. Applying writes them into each field's `format`; *As stored* writes none.

5.2 The viewer's pop-up draws a value through its format, in the title as in the rows.

## 6. Consequences

**Positive.** Dates read as dates and measurements as measurements, in Studio and in ArcGIS clients.

**Negative.** No text or media elements, no Arcade.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ArcGIS clients honour these `dateFormat` names | `INFERRED` — the specification's list, not run against Pro here |

## 8. Dependencies

**Depends on:** ADR-110 (a web map sets its layers' pop-ups).

**Depended on by:** —

## 9. Revisit triggers

- Somebody needs text, media or Arcade in a pop-up.

## 10. Conditions

1. **A number and a date are drawn in the format the pop-up gives them** — **DISCHARGED 2026-10-01**,
   `WebMapViewerTests`.
