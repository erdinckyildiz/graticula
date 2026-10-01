# ADR-129 — Several items at once, and a map says which of its layers its readers will not see

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the shapes are Portal's; each action is a sequence of requests the single-item actions already make |
| **Decided** | 2026-10-01, under the owner's standing instruction to make Studio usable as Portal is |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-114](ADR-114-content-folders-and-move.md) (Move takes several items from the list), [ADR-079](ADR-079-a-web-map-is-a-saved-document.md) (what sharing a map says) |

---

## 1. Context

The ArcGIS reviewer's third pass over Studio, 2026-10-01, ranked two gaps among the six that would stop a Portal user
adopting it:

- **My content had no selection.** Every action was one item's, from its row's menu or its page; the console's own note
  said *there is no bulk operation*. An administrator with a hundred services shares, moves and cleans up by ticking.
- **Sharing a map wider than its layers said only a sentence**: *sharing the map does not share its layers*. Portal
  names the layers a map's new readers will not see and offers to share them. The Map Viewer already worked this out
  (`wmCheckSharing`); the map's item page, where Share is, did not.

## 2. Alternatives considered

### Alternative A — Client-side, over the endpoints single items use (chosen)

A tick on each of the reader's own rows and a bar of what can be done to all of them — Share (private, organization,
public), Move to folder, Delete — each item its own request, the result reported per item. The map's Share lists the
layers whose services are shared more narrowly than the scope chosen, by the same comparison the Map Viewer makes,
and offers to set the reader's own to the map's scope.

### Alternative B — Bulk endpoints on the server

**For:** one request, one transaction. **Against:** a second path for each act, with its own checks to keep in step
with the single-item one; Portal's own bulk actions report per item, which a sequence of single requests does
naturally. `/content/move` already takes several items and is used as such.

## 3. Counterarguments to the preferred option

- *Not atomic*: a bulk share can leave half the items changed. Each failure is named, so the reader can see which.
- *Groups are not offered in bulk*: a group share needs its groups chosen per item, which each item's own Share does.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Ticked items are shared one request each and moved in one request | `ContentBulkTests` | this repository |
| A map shared wider than its private image says so, and shares the image on Save when asked | `WebMapImageryTests` | this repository |
| An image service moves into its owner's folder | `ImageryUploadTests.An_imagery_item_has_its_owners_description_tags_and_a_picture` | this repository |

## 5. Decision

My content ticks the reader's own items and shares, moves or deletes them together. Share chooses no scope for the
reader unless every ticked item already has one; what a bulk action did is said in the bar, and what it could not do
stays ticked with the reason. A map's Share names the layers its chosen readers will not see and offers, as a box
applied on Save with the map, to share the reader's own as widely; a bulk share of maps says the same afterwards.

**An image service moves as any item does.** The design review found Move answering *no such item* for every image:
`/content/move` and Portal's `moveItems` looked among feature services only. Both now find an image service by its
service row, as ADR-124 made sharing do.

## 6. Consequences

**Positive.** Day-to-day content management works at the scale of a real catalogue; a shared map does not open empty
for its readers without warning.

**Negative.** None known.

**State.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Studio's parity with Portal is the owner's standing direction | Stated by the owner |

## 8. Dependencies

**Depends on:** ADR-079, ADR-114.

**Depended on by:** —

## 9. Revisit triggers

- A bulk action measured too slow over a catalogue's worth of items.
