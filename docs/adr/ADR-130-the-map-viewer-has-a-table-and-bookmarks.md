# ADR-130 — The Map Viewer has an attribute table and bookmarks

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — both are ArcGIS Map Viewer's shape over requests this server already answers |
| **Decided** | 2026-10-01, under the owner's standing instruction to make Studio usable as Portal is |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-079](ADR-079-a-web-map-is-a-saved-document.md) (what the Map Viewer offers, and that a map keeps its bookmarks) |

---

## 1. Context

The ArcGIS reviewer's third pass, 2026-10-01: the Map Viewer is a place to look at a map more than to author one for
others. Of what it lacks — a table, bookmarks, a time slider, printing, label scale ranges — the first two are what a
Portal author reaches for on every map: the table to check what a layer holds and find a feature, bookmarks to hand
over the places a map is about.

## 2. Alternatives considered

### Alternative A — A table under the map, and the Web Map's own bookmarks (chosen)

The table: a layer's features in the map's filter, by default only those in view and following the map, fifty a page,
a row taking the map to its feature — `query` with a count beside it, as Studio's Data tab already asks. Bookmarks: the
Web Map specification's root `bookmarks`, a name and an extent each, so ArcGIS clients opening the map see them too.

### Alternative B — Send the reader to Studio's Data tab

**Against:** it leaves the map, and the Data tab is one service's, not the map's layer with the map's filter.

## 3. Counterarguments to the preferred option

- The table reads; editing in it is the larger gap (reviewer's first) and is not decided here.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A layer's table opens under the map with a count, a row outlines its feature; a bookmark is kept in the map's document and removed from it | `WebMapViewerTests.A_layers_table_opens_under_the_map_and_a_bookmark_is_kept_with_it` | this repository |

## 5. Decision

A feature layer's row offers its table under the map; the Map tab keeps bookmarks in the map's `bookmarks`.

## 6. Consequences

**Positive.** An author checks a layer and marks the map's places without leaving it; ArcGIS clients read the
bookmarks.

**Negative.** None known.

**State.** None: both live in the map's document or are read live.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Studio's parity with Portal is the owner's standing direction | Stated by the owner |

## 8. Dependencies

**Depends on:** ADR-079.

**Depended on by:** —

## 9. Revisit triggers

- Editing in the browser is decided; the table is where attribute edits would go.
