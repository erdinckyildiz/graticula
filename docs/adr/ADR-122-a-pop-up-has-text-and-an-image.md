# ADR-122 — A pop-up has text and an image

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — two Web Map fields, drawn as text |
| **Decided** | 2026-10-01, by owner decision (*"devam"*, the ArcGIS reviewer's second-pass item 7) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The reviewer: *"There is no … text, media or Arcade."* A pop-up that reads as a sentence — *Inspected on {date} by
{inspector}* — and shows the photo whose address a field holds is what most published maps' pop-ups are. In the Web
Map they are `popupInfo.description` and `popupInfo.mediaInfos`.

## 2. Alternatives considered

### Alternative A — Text and an image from a field (chosen)

**Argument for.** The two most used content elements, in the fields ArcGIS clients read.

### Alternative B — Arcade, charts, several media

**Argument against, for now.** Arcade is a language this viewer does not run; charts and several media are a
content model of their own.

## 3. Counterarguments to the preferred option

- *`description` may hold HTML written by another client.* It is drawn as text — escaped, its fields filled in, its
  line breaks kept — so a pop-up cannot run anything a map's author or a feature's value put in it.
- *An image comes from an address in the data.* Only `http` and `https` are drawn, lazily and without a referrer.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| `popupInfo.description` and `mediaInfos` of type `image` with `value.sourceURL` | ArcGIS Web Map Specification | publicly documented |
| Text is filled in and escaped, an image is drawn from a web address and not from `javascript:` | `WebMapViewerTests.A_layers_pop_up_is_set_in_the_map_and_drawn_from_it` | this repository |

## 5. Decision

5.1 The Pop-up panel chooses its content — a list of fields, or text with `{field}` in it — and an optional image from
a text field holding an address. Applying writes `description` and an image `mediaInfos` entry.

5.2 The viewer draws the text in place of the field list, and the image under it.

## 6. Consequences

**Positive.** Pop-ups read as sentences and show the photo.

**Negative.** No Arcade, no charts, one image.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Drawing `description` as text is acceptable where another client wrote HTML | `INFERRED` — safe rather than faithful |

## 8. Dependencies

**Depends on:** ADR-110 (pop-ups in the map), ADR-118 (formats).

**Depended on by:** —

## 9. Revisit triggers

- Somebody needs Arcade or a chart in a pop-up.

## 10. Conditions

1. **Text and an image are written and drawn safely** — **DISCHARGED 2026-10-01**, `WebMapViewerTests`.
