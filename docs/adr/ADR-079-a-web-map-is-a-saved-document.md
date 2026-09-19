# ADR-079 — A web map is a saved document, and ArcGIS clients can open it

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` on the format · `MEDIUM` on the viewer's shape until the owner has used it |
| **Decided** | 2026-09-19, by owner decision, after the NextGIS Web comparison ([research/nextgis-web-comparison.md](../research/nextgis-web-comparison.md)) named an end-user web map as one of two things Graticula lacks that a user would notice: *"2. Kapsama alalım"*. **That a web map for the people who use the data, not the administrator, is in v1 is the owner's.** **The ArcGIS Web Map format, its own table, the three sharing scopes and the viewer's layout are `INFERRED`** and listed in §11 |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

`view.html` draws **one service** — its layers, a legend, the attributes of what was clicked — and is what the
services directory offers under *View in: Map*. It is an operator's look at a service, and it forgets
everything when the tab closes. Nothing in the product lets a user put three services on one map, filter
one of them, measure a distance, save that, and send it to a colleague.

Both products a user would arrive from have that. NextGIS Web calls it a *web map*: a saved composition
of layers from many resources, with a layer tree, search, identify, measurement and a share link. ArcGIS
calls it the same thing and gives it a public format: a **Web Map** portal item whose data is a JSON
document — operational layers by URL, a basemap, an initial extent — that every ArcGIS client opens: the
Maps SDK with `new WebMap({ portalItem })`, Pro from its portal pane, Field Maps.

This server already speaks the portal half of that: `/sharing/rest` answers search, a user's content, an
item and `items/{id}/data` (`PortalEndpoints`), but every item it lists is a *projection of a service*,
and `/data` answers empty because a service item has no data document. [ADR-056](ADR-056-an-item-is-its-own-thing-and-a-service-is-one-kind.md)
decided that an item is a record of its own and that a service is one kind of it, and its condition 2 says
*the first kind with no service behind it is built before the model is called proved.* **The item table
that decision describes was never written.** A web map is exactly that first kind.

## 2. Alternatives considered

### Alternative A — Extend `view.html` with a URL that lists services

**For.** No storage at all; a map is its address.

**Against.** An address holds no filter a user can edit without editing a URL, cannot be listed, owned or
shared with a scope, and no ArcGIS client can open it. It is a bookmark, not a map.

### Alternative B — A Graticula-shaped map document

**For.** Only the fields we use, named how we like.

**Against.** A format nobody else reads, invented beside a public one that already has every field we need
and is read by the clients this server exists to serve. [ADR-052](ADR-052-the-canonical-symbology-document-is-cim.md)
made the same choice for symbology for the same reason.

### Alternative C — An ArcGIS Web Map document, stored as its own record and listed as a portal item *(chosen)*

**For.** The owner's ArcGIS Pro and the Maps SDK open it with no Graticula code on their side. The viewer
reads the same document, so there is one format and two readers of it, which is how the format gets
tested. It is ADR-056's first item with no service behind it.

**Against.** The Web Map specification is large (bookmarks, popups, pop-up expressions, tables, time
settings…). We write and read a subset, and a document saved by Pro may carry fields our viewer ignores —
§5.2 says what happens to them.

### Alternative D — Build ADR-056's generic item table first, then a web map as a row in it

**For.** It is what ADR-056 describes, and every later kind lands in the same place.

**Against.** It moves ownership and sharing of every service into a new table — the migration ADR-056 §5
describes — before anybody can save a map, on a server whose request path reads those fields on every
call. That is a real piece of work with its own risks, and the web map does not need it to exist. §6 records
the cost of not doing it.

## 3. Counterarguments to the preferred option

- **It is a second place that answers *who may see this*.** ADR-056 §5 decided there would be one row per
  item that answers it. For a web map there *is* one — its own — and nothing else claims to. The rule it
  would break is *two rows for one item*, which this does not do; what it does not do is put services into
  the same table, and that stays ADR-056's open work.
- **Saving a map does not give anybody access to the services in it.** Correct and intended: a layer a
  viewer cannot read is shown as *not available to you* in the layer list rather than hidden, so the map's
  author can see why a colleague sees less.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The format is public and read by ArcGIS clients | *Web map specification*, Esri developer documentation (public) | developers.arcgis.com, web map specification |
| `/sharing/rest/content/items/{id}/data` already answers, and answers empty | `PortalEndpoints.ItemDataAsync` | this repository |
| A saved map round-trips: saved, listed in search and a user's content, its data read back unchanged | `WebMapStoreTests`, `WebMapEndpointTests` | tests |
| The viewer opens a saved map and every screen passes the UX review | `WebMapViewerTests`, the ux-designer review recorded in §4a | tests, this ADR |

## 5. Decision

1. **A web map is a record in the platform catalogue** — `web_map` (migration 52): a 32-hex id, a title, a
   snippet, an owner, a sharing scope (`private`, `organization`, `public` — stored in the words the
   `service` table uses; the portal reports `organization` as `org`, and the API accepts either), the
   document as `jsonb`, created and modified. **It is authoritative for its own ownership and sharing**,
   as ADR-056 §5 requires of an item. **A member's maps are among their holdings** (ADR-015 §6c): a
   transfer moves them with the services and folders, and a removal that deletes what the member owned
   takes them too.
2. **The document is an ArcGIS Web Map.** The viewer writes, and the server validates, a subset:
   `operationalLayers` (`ArcGISFeatureLayer`, `VectorTileLayer`, `ArcGISMapServiceLayer`, each with `url`,
   `title`, `visibility`, `opacity` and, for a feature layer, `layerDefinition.definitionExpression`),
   `baseMap`, `initialState.viewpoint`, `spatialReference`, `version`. **Every other field is kept exactly as
   it was saved** — a map saved by Pro and opened here and saved again loses nothing it carried, even what
   the viewer does not draw. The server refuses a document that is not an object or is larger than 1 MB,
   and nothing else about its content; the viewer is where the subset is read.
3. **The API.** `GET/POST /content/webmaps`, `GET/PUT/DELETE /content/webmaps/{id}` — beside Studio's
   `/content/items` and `/content/layers` (ADR-034 §5f) rather than under `/rest`, which is the ArcGIS face
   and where a Graticula-shaped route would be the one address no ArcGIS client knows; ArcGIS clients reach a
   map through the portal. *(This read `/rest/webmaps` until the implementation, 2026-09-19.)* Create needs
   `content:create`; change and delete need the owner or `admin:manageAllContent`; read follows the sharing
   scope, and a map the caller may not read is a 404. Giving a map a wider scope asks for what widening a
   service's does — `sharing:shareToOrganization` or `sharing:shareToPublic` — and only when the scope
   changes. An anonymous reader of a public map is not told its owner's name, the rule Q-127 set for the
   portal. **The portal lists it as an item** of type `Web Map`
   in `search`, in its owner's `content/users/{username}` and at `content/items/{id}`, and
   `content/items/{id}/data` returns the document — which is what lets an ArcGIS client open it.
4. **The viewer is `/studio/webmap.html`**, on the vendored OpenLayers ([ADR-020](ADR-020-admin-console-and-service-status.md) §4).
   Its layout is taken from NextGIS Web's web map, which is what the owner compared against: a map with a
   side panel whose tabs are **Layers** (the map's layers with visibility, opacity, order, a filter per
   feature layer, and *Add layer* from the services the viewer can read), **Search** (attribute search
   across the map's feature layers), **Measure** (distance and area, geodesic) and **Map** (title,
   description, sharing, *Save* and *Save as*). A click identifies across every visible feature layer at
   once. `?service=<folder/name>` opens a new, unsaved map with that service in it — which is what the
   services directory's *View in: Map Viewer* link uses.
5. **Studio lists maps** on *My content*, beside services, with *Open* and *Delete*, and *New map*.
6. **`view.html` stays** as the operator's look at one service. The two pages answer different people.

## 6. Consequences

**Positive.** A user can make, keep and share a map without an administrator, and open it in Pro. ADR-056
condition 2 has its first kind with no service behind it.

**Negative.**
- ADR-056's item table is still not written; services and web maps are listed from two sources by the
  portal. The next item kind should build the table rather than add a third source — condition 3.
- No group sharing for maps (services have it). Condition 4.
- The subset the viewer draws will disappoint somebody who saved a map with pop-ups in Pro.

**State.** One catalogue table, `web_map` (migration 52): each map's owner, sharing scope and document. Nothing held at runtime and nothing node-local; every node reads the same rows.

**Ports created.** None.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | An ArcGIS client opens a Web Map item from `/sharing/rest` given `items/{id}` and `items/{id}/data` | untested — condition 1 |

## 8. Dependencies

**Depends on:** ADR-056 (items), ADR-020 §4 (OpenLayers vendored), ADR-018 (privileges), ADR-023 (the
services directory), ADR-034 (Server and Studio).

**Depended on by:** —

## 9. Conditions

1. **A map saved here is opened by the Maps SDK and by Pro** against a running server, and one saved by Pro
   is opened here — both directions, recorded in §4.
2. **Every screen passes the ux-designer review**, first-run state included (no maps yet, no services).
3. **The next item kind builds ADR-056's table** and moves web maps into it, rather than becoming a third
   source for the portal listing.
4. **Group sharing for maps** — built, or recorded as not wanted.

## 10. Revisit triggers

- A second non-service item kind is asked for (condition 3).
- A user saves a map in Pro with fields the viewer must draw to be useful — pop-ups first.

## 10a. Dissent

**Two of the ux-designer review's recommendations (2026-09-19) were not taken, and they are recorded
rather than dropped.** Folding each layer card's controls — zoom to, remove, opacity, filter — into a
per-layer menu, as NextGIS Web and ArcGIS Map Viewer do; and a thumbnail and layer count for each map in
Studio's Maps list. Both are shape decisions the owner has not seen, and neither blocks making, saving or
opening a map. They wait for the owner's first use of the viewer, which is condition 2's other half.

## 11. Inferred, for confirmation

- **The ArcGIS Web Map format** rather than a Graticula document.
- **Its own table** rather than building ADR-056's item table first.
- **Three sharing scopes and no groups** for a first cut.
- **NextGIS Web's layout** for the viewer — the product the owner compared against.
- **`view.html` stays** beside the new viewer.
