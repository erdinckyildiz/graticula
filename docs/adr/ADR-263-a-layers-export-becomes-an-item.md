# ADR-263 — A layer's export becomes an item through the portal

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — the export is ADR-106's, unchanged; which item kind each file is, and the one-layer rule for GeoJSON, are this session's |
| **Decided** | 2026-11-03, by owner decision. The ArcGIS veteran's review of v1.0.499 listed it as #6, *item.export (ADR-106 refused, its premise "no addItem" is gone)*; asked which of the review's open items to take, the owner chose all four, this one among them. |
| **Supersedes** | [ADR-106](ADR-106-a-layer-s-data-can-be-exported-as-a-file.md)'s decision 1 in part — *not a new portal item* — for the portal's door only |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

ADR-106 made a layer's export a file kept for a limited time, and said `item.export()` would not work, because an export
in ArcGIS makes a portal item and there was no item table to hold one. Its first revisit trigger was *ADR-056's item
table is built*. It is: documents, web maps, services and toolboxes are items, and `addItem` keeps a file as a document
([ADR-257](ADR-257-portal-content-adds-are-doors-onto-import.md)).

`item.export(title, export_format)` posts `content/users/{u}/export`, takes `exportItemId` and `jobId` from the answer,
asks `items/{exportItemId}/status?jobType=export` until it is `completed`, and returns the item.

## 2. Alternatives considered

### Alternative A — Keep ADR-106's decision

The trigger it named has happened. Not chosen.

### Alternative B — Export into the item synchronously

A large layer's export takes minutes; ADR-106 made it a job for that reason. Not chosen.

### Alternative C — The item at once, its file when the job writes it *(chosen)*

## 3. Decision

1. **`content/users/{u}/export`** takes `itemId` (a feature layer's item, by any of its faces' ids), `exportFormat` (ADR-106's
   eight, in ArcGIS's spellings), `title`, `snippet` and `exportParameters`' `layers`. It runs Studio's Start inside the
   request, so who may export, the formats, the row cap, the budget, the one-export-at-a-time rule and the audit line are
   ADR-106's.
2. **The item is made at once** — a private document of the caller's, empty — and answered as `exportItemId` with the job's
   id, so a client can open it and ask its status before the file exists.
3. **Its status is the job's**: `processing` with the job's percent, then `completed` once the written file has been copied
   into the item, or `failed` with the job's reason. A file larger than a document may be here (`DocumentMaxMB`, 64 MB
   by default) fails with that reason, and stays downloadable from Studio's *Export data* for ADR-106's retention.
4. **The item's kind is the file's**: GeoPackage, File Geodatabase, Shapefile, Microsoft Excel, KML, CSV, a CSV Collection
   (several layers' CSVs zipped), GeoJson or Feature Collection.
5. **GeoJSON and Feature Collection are one layer.** Several layers in either are a zip no item type names, so the export
   is refused and the answer says which formats hold several. *(INFERRED, and confirmed by the owner 2026-11-04:
   "Reddetsin".)*
6. **Which export an item waits for is held in the process**, at most 256 at once, the oldest forgotten first; a restart
   before the job finishes leaves the item empty, and its status no longer says *processing*.

## 4. Consequences

- **State.** The item is a document in the catalogue. Which export it waits for is node-local, in the process that started it: on a site of several nodes, a status asked of another node does not see the wait and answers as for any document, so a client polling through a balancer may read an empty item as complete. Recorded rather than solved; a single node, the baseline, is unaffected.
- `item.export()` works from the Python API, and so does ArcGIS Online's *Export Data* as scripts call it.
- An export item is a document like an uploaded file: listed in My content, shared, deleted and recycled as one.
- ADR-106 stays the export; this is a door onto it, and its file-kept-for-a-while answer is unchanged for Studio.

## 5. Conditions

None.

## 6. Dependencies

- [ADR-106](ADR-106-a-layer-s-data-can-be-exported-as-a-file.md) — the export itself.
- [ADR-188](ADR-188-a-document-is-an-item.md) — documents as items.
- [ADR-214](ADR-214-the-portals-item-writes-do-what-they-answer.md) §5a — a door that runs Studio's handler.
