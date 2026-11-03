# ADR-264 — An app or a link is an item, and an item keeps resources

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` — a store of what clients send, which the clients' own documentation describes; which types are kept, and the resource limits, are this session's |
| **Decided** | 2026-11-03, by owner decision. The ArcGIS veteran's review of v1.0.499 listed it as #1 and its one **BLOCKER for the app layer**: *app and URL items (Dashboards, Experience Builder, Survey123, a Feature Service by URL) and item resources*. Asked which of the review's open items to take, the owner chose all four, this one among them. |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

ArcGIS's app layer — Dashboards, Experience Builder, Survey123, StoryMaps, the configurable apps — keeps each app as a
portal item: an item of the app's type whose data is the app's JSON, often with files beside it as **item resources**
(Experience Builder's configuration and images live there). A service on another server is added to a portal by its
URL, as an item of the service's type that points at it.

`addItem` here accepted a service definition, a web map, and files Studio keeps
([ADR-191](ADR-191-arcgis-pro-publishes-hosted-feature-layers.md), [ADR-257](ADR-257-portal-content-adds-are-doors-onto-import.md)),
and refused everything else by name; `items/{id}/resources` was a 404. So none of those clients could save an app here,
and nothing could register another server's service.

## 2. Alternatives considered

### Alternative A — Leave the app layer out

The review's one blocker stays one. Not chosen.

### Alternative B — A new kind of item, `application`, with its own table

A fourth kind would need its own listing, sharing, ownership, folder, delete and recycle paths, all of which documents
already have. Not chosen.

### Alternative C — An app or a link is a document; resources are any item's *(chosen)*

## 3. Decision

1. **An app or a link is a document** whose content is its JSON (empty for a link with none), with two more columns
   (migration 128): `url` and `type_keywords`. The kinds are Dashboard, Web Experience, Web Mapping Application, Form,
   StoryMap, and Feature Service, Map Service, Vector Tile Service, Image Service and Document Link by URL
   (`Documents.Apps`). It is listed, shared, owned, moved, deleted and recycled as a document is.
2. **`addItem`** of one of those types takes `text` (JSON, within the document limit), `url` (absolute http or https,
   at most 2,048 characters), or both, with `typeKeywords`, `tags`, `snippet` and `description`, under the create
   privilege. **`update`** writes the same three; **`items/{id}/data`** answers its JSON as JSON, and `{}` for a link with
   none. The item says its `url` and its `typeKeywords`.
3. **Kept, not run.** This server stores an app's item and resources for the app's own client to read. It does not
   serve Dashboards, Experience Builder or Survey123, and a link's service answers at its own address.
4. **Any item keeps resources** (`item_resource`, migration 128): `addResources` (which does not replace),
   `updateResources` (which does not add), `removeResources` (one, or `deleteAll`), a page of them at
   `items/{id}/resources`, and each at `items/{id}/resources/{path}`. A `file` or a `text`, at `resourcesPrefix/fileName`,
   with no `..` and no leading `/`; at most 100 MB per item together. Written by whoever may change the item; read by
   whoever may read it, and a `private` one only by its owner or whoever may manage all content. A resource is served
   with `nosniff` and a sandboxing CSP, never as a page of this site.
5. **Studio shows them on the document page**: an app's tile says APP and a link's LINK; a link opens where it points
   and shows its host in place of a size; neither offers *Replace file*. A Feature Service item whose URL is another
   server's opens there, not as one of this server's services.

## 4. Consequences

- **State.** Two nullable columns on `document` and the `item_resource` table, both in the catalogue, tenant-bound as
  every item table is; nothing at runtime.
- Dashboards, Experience Builder, Survey123 and the configurable apps can save their items and resources here, and
  their clients can read them back; whether each client then runs against this portal is a separate question for each
  client, and is not answered by this decision.
- A registered service's URL is shown and linked, never fetched by this server: no request is made to it on anybody's
  behalf.

## 5. Conditions

None.

## 6. Dependencies

- [ADR-188](ADR-188-a-document-is-an-item.md) — documents as items.
- [ADR-203](ADR-203-a-deleted-item-waits-in-the-recycle-bin.md) — the recycle bin, which keeps an item's rows, resources included.
- [ADR-257](ADR-257-portal-content-adds-are-doors-onto-import.md) — `addItem` as a door onto Studio's store.
