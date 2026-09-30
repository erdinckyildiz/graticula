# ADR-111 — An item carries tags

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — a list of words per item, searched as text; nothing measurable is at stake |
| **Decided** | 2026-10-01, by owner decision (*"Tamamı"*, the third of five questions from the ArcGIS review's second pass) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

The ArcGIS reviewer's second pass: *"Item metadata is thin. There are no tags. Content search matches only name, owner
or folder."* Portal items carry tags, Portal search reads them, and ArcGIS Pro and the ArcGIS API for Python read and
write them on an item. The portal face here answered `tags` with the service's folder name, or nothing for a map.

## 2. Alternatives considered

### Alternative A — A `text[]` on the service and on the web map (chosen)

**Argument for.** Two item kinds exist; each gets one column. The list is read with the item (the catalogue already
reads the service row) and searched in the browser with the rest of the item's words.

### Alternative B — A tag table shared by every item kind

**Argument against, for now.** It is ADR-056's item table in miniature. When that table is written, tags move into
it with the rest; until then a second place to keep an item's facts would be the drift ADR-056 warns about.

## 3. Counterarguments to the preferred option

- *No tag suggestions or a tag cloud.* Portal offers the tags already used; this does not yet.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Portal items carry `tags`, and clients read them | ArcGIS REST *Item* | publicly documented |

## 5. Decision

5.1 Migration 70: `service.tags` and `web_map.tags`, `text[]`, empty by default.

5.2 `PUT /admin/services/{name}/tags?folder=…` (the owner or an administrator) and `PUT /content/webmaps/{id}/tags`
(whoever may change the map), body `{"tags": [...]}`. Stored trimmed, the same word once whatever its case, empties
dropped; at most 32 tags of at most 64 characters, more is refused.

5.3 Listed with the item (`/content/items`, the map's answer), said by the portal face as the item's `tags` (a
service keeps its folder among them, as before), and searched by My content.

5.4 In Studio, a service's tags are edited with its description on Overview; a map's with its title and summary.

## 6. Consequences

**Positive.** Items are found by what they are about.

**Negative.** Two columns rather than one table; no suggestions.

**State.** One column per item kind in the catalogue; nothing at runtime.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | 32 tags of 64 characters is enough for an item | `INFERRED` |

## 8. Dependencies

**Depends on:** ADR-056 (the item table tags move into), ADR-075 (who changes an item).

**Depended on by:** —

## 9. Revisit triggers

- ADR-056's item table is written.

## 10. Conditions

1. **Tags are set, tidied, listed and said by the portal face** — **DISCHARGED 2026-10-01**, `ItemTagsConformanceTests`.
2. **Studio edits them** — **DISCHARGED 2026-10-01**, `ItemStructureTests.Tags_are_edited_with_the_description`.
