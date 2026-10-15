# ADR-184 — The basemap gallery is a group, and its vector tile services are basemaps

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-15, by owner decision. After the showcase's map ground was cleared at their request (*"zemini boşaltıp osm kullanalım"*), the owner asked for the default basemap to be chosen *"arcgis enterprisetaki gibi"*, chose Enterprise's gallery model over a fixed two-way choice when asked, and set its content: *"insanlar vector tile olarak kendi basemaplerini tanımlayabilir olmalı. bunun için belirli bir gruptaki tüm vector tile haritalar basemap olarak setlenebilir."* |
| **Supersedes** | — (amends [ADR-086](ADR-086-the-operator-chooses-the-map-ground.md) §5.3: the portal's default basemap is the gallery's chosen one when there is one) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

[ADR-086](ADR-086-the-operator-chooses-the-map-ground.md) gave the operator one ground: a stack of this server's
vector tile services, drawn under every map and published as `portals/self.defaultBasemap`. With nothing chosen,
OpenStreetMap. There was no way to offer *several* basemaps and pick one as the default, which is what an ArcGIS
Enterprise organisation does: its administrator names a **basemap gallery group** and chooses the **default
basemap** from it, and Map Viewer and Pro offer the gallery's maps in their basemap pickers.

The owner wants members to be able to make their own basemaps from vector tiles, and every vector tile map in
one group to be eligible.

## 2. Alternatives considered

### Alternative A — a group of vector tile services, each served to clients as a basemap Web Map *(chosen)*

**Argument for.** The owner's words: share a vector tile service with the gallery group and it is a basemap —
nothing else to author. ArcGIS clients build their gallery from **Web Map items only**: the Maps SDK's
`Portal.fetchBasemaps` runs `basemapGalleryGroupQuery` against `community/groups`, takes the first group, queries
its items with `type:"Web Map" -type:"Web Application"`, and keeps the results whose `type` is `Web Map` (read in
`esri/portal/Portal.js`, 4.31, the module the gallery's `PortalBasemapsSource` calls). So each service in the
group is answered *in that group's item search* as a Web Map item of its own, whose data is a `baseMap` of that
one `VectorTileLayer`. Saved web maps shared with the group are basemaps too, as their own items, which is how a
basemap of several tile services stacked — districts over land — is made.

**Argument against.** An item that exists only because of a share is a second identity for the service; a
client that saves the gallery entry's id holds an id that disappears if the share is removed.

### Alternative B — a group of Web Maps only, as Enterprise has it

**Argument for.** No synthesised item; exactly Enterprise's model.

**Argument against.** Every basemap is two acts — publish the tiles, then author and share a web map around
them — where the owner asked for one. And not the owner's words.

### Alternative C — a fixed choice between OpenStreetMap and the ground

**Argument for.** A few lines. Offered to the owner as the recommendation and not chosen.

**Argument against.** No gallery, and nothing a member can add.

## 3. Counterarguments to the preferred option

- **Q-119 says no group is visible to an anonymous caller**, and a showcase visitor's Map Viewer is anonymous, so
  a signed-out visitor has the default basemap and no gallery to choose from. That was first built the other way —
  the gallery group answered to everybody, `INFERRED` — and the owner decided against it the same day (§5.4).
- **A synthesised Web Map is not editable**: `content/items/{id}/update` has nothing to write to. A client that
  offers *Save as* on a gallery entry saves a new map, which is the right outcome.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The SDK's gallery reads Web Map items of the first group the query finds | `fetchBasemaps`: `queryGroups(basemapGalleryGroupQuery)` → `results[0].queryItems({query:'type:"Web Map" -type:"Web Application"', num:100})` → `filter(type === "Web Map")` | `https://js.arcgis.com/4.31/esri/portal/Portal.js`, read 2026-10-15 |
| The gallery widget re-reads when the portal's query changes | `PortalBasemapsSource` watches `portal.basemapGalleryGroupQuery` | `https://js.arcgis.com/4.31/esri/widgets/BasemapGallery/support/PortalBasemapsSource.js` |
| The SDK lists a group's items through `content/groups/{id}/search`, not `search?q=group:` | Maps SDK 4.29's `BasemapGallery`, signed out against the local fixture, failed with a 404 on `/sharing/rest/content/groups/<id>/search?q=type:"Web Map" -type:"Web Application"&sortField=name` — an endpoint this portal did not have. Added (§5.4) | `tools/sdk-probe/basemap-gallery.html` with `drive.py`, 2026-10-15 |
| The SDK's own gallery lists the entry and opens on the default | Same page after the endpoint: `fetchBasemaps` returned one basemap, `ci_parcels`, a `vector-tile` layer at its `root.json`; the widget drew its card with the service's thumbnail, selected, and `map.basemap` was that layer | same |
| The gallery and the default are served and drawn | `TheBasemapGalleryIsAGroupTests` (conformance) and `WebMapOffersTheBasemapGalleryTests` (console) | this repository |

## 5. Decision

5.1 **Two server settings, beside ADR-086's ground**: `basemap_gallery`, a group's id, and `default_basemap`,
either `osm` or the id of an item in the gallery. Set together by `PUT /admin/settings/basemaps` with
`admin:manageServer`; `GET` answers them with every group the server has and the gallery's entries.

5.2 **The gallery's entries** are, for whoever is asking: each running vector tile service shared with the group
that the caller may draw and that is tiled in Web Mercator, as a Web Map item whose id is the service's
`Basemap` face id; and each saved web map shared with the group that the caller may read. The synthesised item's
data is a Web Map with no operational layers and a `baseMap` of one `VectorTileLayer`, its `id` the service's
qualified name — the spelling ADR-086's ground already uses, so `ground.js` reads it unchanged.

5.3 **`portals/self`** names the gallery as `basemapGalleryGroupQuery` and `vectorBasemapGalleryGroupQuery`
(`id:<group>`), and `defaultBasemap` is the chosen entry's `baseMap` when the caller may read it. When there is no
choice, or the caller may not read it, the order is ADR-086's: the ground, then OpenStreetMap. `osm` chosen is
OpenStreetMap even when a ground is set.

5.4 **The gallery group is found by every signed-in caller, and by nobody signed out.** `community/groups` answers
it to a signed-in caller outside it with the fields every outsider is already shown (ADR-036 §4g). **A signed-out
caller is not given it — owner decision 2026-10-15, Q-174: *"giriş yapmamış kullanıcı basemap seçemesin"*** — so
Q-119's rule stands whole, and a signed-out caller is given the default basemap (§5.3, which depends on the
default's own sharing, not on the group) and nothing to choose from. The item search answers `group:<gallery>` with
the entries §5.2 gives *that* caller. A group's own content search, `content/groups/{id}/search`, answers the same listing narrowed to the group,
because that is where the SDK asks.

5.5 **The pages offer the gallery.** Map Viewer's basemap list holds the gallery's entries, OpenStreetMap and
*None*; a new map starts on `portals/self`'s default. The console's Settings screen chooses the gallery group and
the default from its entries.

**Revised after the design review of 2026-10-15**, which found *No gallery* jumping back to the saved group so the
gallery could not be turned off; the Map ground card still saying *nothing chosen: OpenStreetMap* while a gallery
default was in force; the basemap list hidden from a signed-out reader, whom ArcGIS's Map Viewer lets switch; the
group preview unannounced and the list disabled between arrow keys; and no next step when the server has no group.
Each is repaired except the reader's: *No gallery* previews as a choice, the ground card says when it is not in use,
the preview is a live region and a late answer is not drawn over a newer choice. **A signed-out reader does not
switch the basemap** — briefly built, and then decided against by the owner (§5.4).
**Not repaired:** a new map opens at world scale on a default that covers a city, and shows little there; opening
on the default's extent is a change to how every new map opens, and is left for when it is asked for.

## 6. Consequences

**Positive.** A member makes a basemap by sharing a vector tile service with one group; Pro, Map Viewer and this
server's pages offer it.

**Negative.** `portals/self` reads two settings more, and a gallery item is a search over the catalogue.

**Ports created.** None.

**State.** Two rows in `server_setting`. No migration.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | Pro builds its basemap gallery from the same query the SDK does | Not observed — condition 2 |

## 8. Dependencies

**Depends on:** [ADR-086](ADR-086-the-operator-chooses-the-map-ground.md), [ADR-036](ADR-036-groups.md) (groups),
[ADR-079](ADR-079-a-web-map-is-a-saved-document.md) (web maps as portal items).

**Depended on by:** —

## 9. Revisit triggers

- A raster tile service asked for as a basemap.
- A client that keeps a synthesised entry's id and loses it when the share is removed.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The Maps SDK's own BasemapGallery lists the gallery against this server**, and its default draws.
   **DISCHARGED 2026-10-15** against the local fixture with Maps SDK 4.29 (§4): the first run found the missing
   `content/groups/{id}/search`; after it, the widget listed `ci_parcels` with its thumbnail and the map's basemap
   was the gallery's default. That run was signed out, while §5.4 still answered the group to everybody; signed in,
   the SDK makes the same three requests, which `TheBasemapGalleryIsAGroupTests` walks with a token. Not yet run
   against the showcase, which has no gallery group.
2. **ArcGIS Pro's basemap gallery lists it** when signed in to this portal.
3. ~~**`INFERRED`: the gallery group is visible to a signed-out caller (§5.4)**~~ **DISCHARGED 2026-10-15 by owner
   decision, the other way:** asked whether a signed-out caller still gets the default without the group, and told
   yes, the owner answered *"giriş yapmamış kullanıcı basemap seçemesin"*. The group is answered to signed-in callers
   only, and a signed-out reader's Map Viewer offers no basemap list (Q-174).
