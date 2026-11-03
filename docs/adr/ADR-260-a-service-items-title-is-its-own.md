# ADR-260 — A service item's title is its own

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` — the column existed and was written once; this writes it again and reads it where the name was read |
| **Decided** | 2026-11-03, by owner decision. The ArcGIS veteran's review of v1.0.499 listed it as #9; asked which of the review's open items to take, the owner chose all four, this one among them. |
| **Supersedes** | [ADR-214](ADR-214-the-portals-item-writes-do-what-they-answer.md) §5.4, *"A service's title is its name: an `update` with another title is refused rather than renaming the service"* |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

A Portal item has a title, and a hosted service has a name. They start the same when Pro shares a layer, and they are not
the same thing: the name is the service's address — in its URL, in every web map that holds it, in every client that saved
it — and may hold only what a URL may; the title is what people call the item, may hold spaces and capitals and
punctuation, and is changed freely from the item's page or by the ArcGIS API for Python's `item.update(title=…)`.

Here they were one. Migration 87 gave every item a `title` column and wrote the service's name into it, and nothing wrote
it again; the portal's item said `title = service.Name`; and ADR-214 §5.4 refused an update with another title, because the
only way to honour it at the time looked like renaming the service. So `item.update(title="Roads of Ankara, 2026")`
answered an error, Pro's *Name* field lost its spaces, and Studio offered no title to edit.

## 2. Alternatives considered

### Alternative A — Keep §5.4: the title is the name

Honest, and it leaves a Python script or a Pro user with no way to call a service what they call it. Not chosen.

### Alternative B — A title update renames the service

What §5.4 was refusing to do, rightly: a rename moves the address and breaks every web map and saved client that holds
it. Not chosen.

### Alternative C — The title is the item's column, written and read as such *(chosen)*

The column is there. Writing it touches nothing a URL holds.

## 3. Decision

1. **A service item's title is `item.title`.** The catalogue reads it beside the name (`PublishedService.Title`, the name
   when nothing else was given), and the portal's item, its search, its basemap gallery and Studio's lists say it.
2. **The portal's `update` writes it** — at most 250 characters, a web map title's limit — and leaves the name alone. The
   refusal of §5.4 is gone; the other refusals of §5 stand.
3. **Studio's *Edit details* starts with Title**, written by `PUT /admin/services/{name}/title` under the description's
   rules (its owner or whoever manages it; audited as `service.title`). An empty title is the name again. The item page is
   headed by the title, and says the service name among its facts when the two differ.
4. **Pro's first publish takes the title from the pane's name**, as `esriinfo/iteminfo.xml` carries it. An overwrite does
   not, so a title changed since is kept.
5. **A layer published from a data item takes that item's title** (a CSV, a GeoJSON, a geodatabase added to the portal
   and published — [ADR-257](ADR-257-portal-content-adds-are-doors-onto-import.md) §5.5, which it amends). **INFERRED, for
   the owner to confirm:** that ArcGIS's published layer is called what its source item is called was read from how the
   Python API's `publish()` names its result by default, not from a statement the owner made.
6. **The name does not change through any of these.** Renaming a service stays the server's own act.

## 4. Consequences

- **State.** The catalogue's existing `item.title`, now written for a service; nothing at runtime beyond the catalogue's own copy of it.
- `item.update(title=…)` from the Python API and Pro's item details now succeed, and a search by `title:` finds the new
  title.
- Every service published before this keeps its name as its title, because that is what migration 87 wrote. No migration.
- A list that shows a service by its name rather than its item's title is now a list that can disagree with the portal;
  Studio's content list, its item page and its search read the title, and Server's service list keeps the name, which is
  what an administrator addresses.

## 5. Conditions

None.

## 6. Dependencies

- [ADR-056](ADR-056-an-item-is-its-own-thing-and-a-service-is-one-kind.md) — the item as its own record, and migration 87's `title`.
- [ADR-214](ADR-214-the-portals-item-writes-do-what-they-answer.md) — the item writes, whose §5.4 this replaces.
- [ADR-191](ADR-191-arcgis-pro-publishes-hosted-feature-layers.md) — Pro's publish and its item information.
