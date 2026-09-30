# ADR-113 — A hosted feature layer has views, and a view is a PostgreSQL view

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — PostgreSQL's automatically updatable views were measured doing what this needs; the surfaces around them are many |
| **Decided** | 2026-10-01, by owner decision (*"Tamamı"*, the second of five questions from the ArcGIS review's second pass) |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

Portal's *Create View Layer* makes a second item and a second service URL over the same data: the same rows, edited
through either, with its own layers, fields, filter, editing and sharing. It is what an organisation uses to publish a
public read-only face of a private editable layer, and what Survey123 and Field Maps are built on — an anonymous
*add only* view in front of a private source. The ArcGIS reviewer ranked it the third most missed item.

What a v1 view must do, from that review: its own name, URL and item; the source's rows, not a copy, with the source's
layer ids; a subset of layers and of fields; a **server-side** filter that reads, counts, extents, statistics and tiles
obey, and that updates and deletes cannot reach past; its own capabilities and sharing, neither derived from the
source's; no *Update data* on a view; a source with views refuses overwrite and deletion; `isView` / `hasViews` in the
REST documents and `View Service` among the item's keywords; *Views* on the source's page and *Source* on the view's.

The code as it stood (surveyed 2026-10-01): two services may already publish one table (migration 40); hidden fields
are enforced on every face including edits (field overrides, ADR-063); capabilities are per service. There was **no
row filter anywhere**, and the predicate would have had to be added separately to query, count, extent, ids,
statistics, the layer document's extent, related records, attachments, tiles, update and delete — eleven places, each
one missed being a view that leaks the rows it was made to hide.

## 2. Alternatives considered

### Alternative A — A PostgreSQL view per view layer (chosen)

`create view hosted.<view table> as select * from hosted.<source table> where <filter>`. **Measured on PostGIS 3 /
PostgreSQL 16 before it was chosen**: such a view is automatically updatable; an insert through it takes the base
table's identity default and fires its triggers (editor tracking, history); an update of a row outside the filter
changes nothing (`UPDATE 0`); the planner pushes a bounding box through to the base table's GiST index; PostGIS lists
the view in `geometry_columns` with its type and SRID.

**Argument for.** Every face that reads or writes the layer already reads and writes *a relation by name*; the view
layer names the view. The filter is enforced by the database in one place, and a face added next year obeys it without
knowing it exists. The source's table cannot be dropped while a view depends on it — PostgreSQL refuses, and the
importer's `DropAsync` deliberately never cascades.

### Alternative B — A predicate carried on the layer and added in each face

**Argument against.** The eleven places above, and every future one. The failure mode is silent: a face that forgets
the predicate answers with the rows the view exists to hide.

### Alternative C — A copy of the data

**Argument against.** It is not a view. Edits through one would not appear in the other, which the reviewer named as
the first thing that would make an ArcGIS user distrust it.

## 3. Counterarguments to the preferred option

- *DDL built from a filter somebody typed.* The filter is parsed by `WhereClause` against the source's columns — the
  same parser every query's `where` goes through, which re-emits SQL from a tree and binds every literal. For DDL the
  bound values are rendered by PostgreSQL's own `quote_nullable`, so no literal is ever quoted by this code.
- *A view's column list is fixed when it is made.* A field added to the source later is not in the view until the view
  is remade — which is what Portal does too (a new source field is not shown by a view until it is added to it). A field
  **dropped** from the source is refused while views exist, because every view depends on every column.
- *Tiles are cached per layer.* An edit through the source must also purge its views' tiles, and the reverse. Each
  layer carries the ids of the layers that show its rows under another service (`ViewLayers`), and every purge after
  an edit, an append, an overwrite or a truncate empties those too.
- *A PostgreSQL view has no system columns*, and the writer's optimistic concurrency reads `xmin`. The view carries
  `xmin` as a column — measured: it reads, and `returning xmin` after an update through the view gives the new row's —
  and describe leaves an `xid` column out of the fields.
- *`CURRENT_DATE` in a filter is fixed when the view is made*, because the parser evaluates it and DDL receives the
  value. A filter such as *the last thirty days* needs remaking; said in the dialog's hint rather than hidden.
- *A view's attachments are its source's only when the source had attachments when the view was made*: the view
  over the attachment table is made then. Attachments turned on later need the view remade (changing its filter does).
- *A view layer is not called what its source's is.* Portal keeps the source's layer names; here the admin surface
  finds a layer by its name, and a second layer with the source's name would make every one of the source's pages
  ambiguous (409). A single-layer view's layer takes the view's name, as an import's does; a multi-layer view's are
  `<view>_<source layer>`. Recorded as a difference from Portal.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| An automatically updatable view takes base defaults, fires base triggers, cannot update outside its filter, uses the base index | probe on the local PostGIS, 2026-10-01 | this repository's session log |
| Portal views: separate item and URL, same data, own fields/filter/capabilities/sharing, `isView`, `View Service` | ArcGIS Online / Enterprise *Create hosted feature layer views* | publicly documented |

## 5. Decision

5.1 **Catalogue.** Migration 71: `service.view_of uuid null references service(id)` — the source, for a view — and
`layer.view_definition text null`, the filter as it was written. A view service's layers keep the source layers'
indices.

5.2 **Data.** One PostgreSQL view per view layer in the hosted schema, over the source layer's table, with the filter
as its `where`; one more over the source's attachment table, filtered to the rows the first can see, when the source
has attachments. Changing a filter replaces the view (`create or replace view`).

5.3 **Creating.** `POST /admin/services/{name}/views?folder=…` (whoever manages the source), body `name`, `layers`
(source layer ids, all when absent), `definitions` (layer id → filter), `capabilities` (a subset of Query, Create,
Update, Delete; Query when absent), `sharing` (private when absent). The view is owned by its creator. `PUT
/admin/services/{view}/views/{layerId}/definition` changes one filter. Fields are then chosen on the view's own Fields
page (field overrides), where hiding one hides it from that view only.

5.4 **Refusals.** On a view: append, overwrite, truncate, adding and dropping fields, tracking, global ids — *a view has
no data of its own; change its source*. On a source with views: overwrite, dropping a field, and deleting the service —
naming the views. Deleting a view drops its PostgreSQL views and never the source's table. Deleting any service with
`drop=true` no longer drops a table another service still publishes (a risk since migration 40, found while surveying
for this).

5.5 **Faces.** The view's FeatureServer and layer documents say `isView: true` and `isUpdatableView`; the source's say
`hasViews: true`. The portal item of a view carries `View Service` in `typeKeywords`. Studio's service page lists a
source's views and links a view to its source; *Create view* is on the source's page.

## 6. Consequences

**Positive.** Every face obeys the filter because the database does. Survey-style *add only* public views work.

**Negative.** A view's history and feature-level history reads are not offered (the history table is the source's).
A new source field needs the view remade to appear.

**State.** Two catalogue columns; one or two PostgreSQL views per view layer.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Every read and write of a hosted layer goes through the relation its catalogue row names | `INFERRED` — surveyed, not proved; condition 2 tests it |

## 8. Dependencies

**Depends on:** ADR-063 (field overrides), ADR-075 (who changes an item), ADR-103 (Update data).

**Depended on by:** —

## 9. Revisit triggers

- A face is added that reads a hosted layer's table by a name of its own making.
- Somebody needs a join view or a view across two sources.

## 10. Conditions

1. **A view is created over a source and reads only its filter's rows, through query, count and tiles** —
   **DISCHARGED 2026-10-01**, `HostedViewsConformanceTests`: query, count, and a vector tile that holds the source's
   filtered-out row while the view's does not.
2. **An update or delete through a view cannot reach a row outside its filter, and an add through it lands in the
   source** — **DISCHARGED 2026-10-01**, `HostedViewsConformanceTests` (an update past the filter answers *no feature
   with object id*, and an add-only view's add is counted in the source).
3. **The refusals in §5.4 hold, and deleting a view leaves the source's table** — **DISCHARGED 2026-10-01**,
   `HostedViewsConformanceTests`.
4. **Studio creates a view and shows both links** — **DISCHARGED 2026-10-01**, `HostedViewPagesTests`.
