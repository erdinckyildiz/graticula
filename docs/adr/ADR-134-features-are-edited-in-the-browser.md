# ADR-134 — Features are edited in the browser

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — every write goes through the layer's own `applyEdits`, which ArcGIS clients already use here |
| **Decided** | 2026-10-01 by owner decision (*"1 ve 2 onayladım"*, answering whether to build editing in the browser) |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-079](ADR-079-a-web-map-is-a-saved-document.md) (what the Map Viewer offers) |

---

## 1. Context

The ArcGIS reviewer's third pass over Studio, 2026-10-01, ranked first the gap that would stop a Portal user adopting
it for daily work: nothing in the browser changes data. Map Viewer's Edit and the item page's Data table are where a
Portal user corrects an attribute, draws a new feature and deletes one; here the console, the Map Viewer and the
viewers made no write at all. The server side was already there: `applyEdits` on every layer, domains enforced,
ownership-based access (ADR-075, ADR-115), editor tracking.

## 2. Alternatives considered

### Alternative A — The browser writes through the layer's `applyEdits`, in three steps (chosen)

The request every ArcGIS client already makes, so the browser is one more client of the same rules: a refusal the
server makes — not the owner's feature, a value outside its domain — is the refusal the reader sees. In the order a
Portal user meets it: attributes in the card a click opens; then drawing and deleting features; then editing in the
table.

### Alternative B — An editing endpoint of the console's own

**Against:** a second write path, whose checks would have to be kept in step with `applyEdits`' — the propagation
shape D-130 records.

## 3. Counterarguments to the preferred option

- The browser cannot know beforehand whether the server will accept an edit (ownership is decided per feature); it
  offers Edit where the layer offers Update and says the server's refusal as it comes.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The card of an editable layer's feature offers Edit; an unchanged form sends nothing; a changed one is sent to the layer's `applyEdits` | `WebMapViewerTests.A_features_attributes_are_edited_in_its_card` | this repository |
| The request the form sends is accepted by the server as an update | `applyEdits` with `updates=[{attributes:{objectid,parcel}}]` against `ci_parcels`, 2026-10-01 | local run |

## 5. Decision

The Map Viewer edits features through their layer's `applyEdits`, offered to a signed-in reader where the layer offers
the operation.

## 6. Consequences

**Positive.** Studio becomes a place to keep data right, not only to look at it.

**Negative.** None known.

**State.** None: the writes are the server's existing ones.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Editing in the browser is wanted | Stated by the owner, 2026-10-01 |

## 8. Dependencies

**Depends on:** ADR-075, ADR-079, ADR-115, the layer's `applyEdits`.

**Depended on by:** —

## 9. Revisit triggers

- A layer type whose edits `applyEdits` does not carry (attachments, relationships).

## 10. Conditions

1. **A feature's attributes are edited in the card a click opens** — **DISCHARGED 2026-10-01**,
   `WebMapViewerTests.A_features_attributes_are_edited_in_its_card`.
2. **A feature is drawn on the map and added, and a feature is deleted** — **DISCHARGED 2026-10-01**,
   `WebMapViewerTests.A_feature_is_drawn_and_added_and_another_is_deleted`. *Add feature* on a layer that offers
   Create draws the layer's shape — by clicking, or from a sketch bar that places points at a crosshair at the map's
   centre, undoes and finishes, so a keyboard or touch reader can draw too — then opens the new feature's form with
   *Create*; a layer with time starts the feature at the time window's end so it is not hidden the moment it exists.
   *Delete* on a card asks, then sends the delete. Two design-review passes; the first found a point's form taken away
   by the click that placed it, and no way to draw without a mouse.
3. **Attributes are edited in the attribute table.**
