# ADR-102 — The Studio item is one page, and every setting has one home

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the structure was drawn by an ArcGIS administrator's review and a design review in turn, each criticising the other's draft, and measured against the running console; it is built in steps (§5.6), and the three questions in §10 are the owner's |
| **Decided** | 2026-10-01, on the owner's instruction: *"item ve yapısı konusu önemli. caching'e gitmek için layer'a tıklaman gerektiğini bilmen lazım … Arcgis direkt button olarak veriyor"*, *"Arcgis symbology'i webmap'e yıkmıştı"*, *"neden her yerde bir symbology düğmesi var"*, and *"ux designer la arcgis veteran birlikte çalışsın. güzel bir yapı çıkarın"*. The structure is theirs to have asked for; its details are the two reviewers' and are reviewable here |
| **Amends** | [ADR-034](ADR-034-server-and-studio.md) §5c (layer pages leave both surfaces; time field, visible range and thumbnail move to Studio; Server keeps the service's server settings and links to the item); [ADR-053](ADR-053-the-symbology-editor-is-three-columns.md) (the editor becomes Visualization's Styles panel); [ADR-093](ADR-093-seeding-the-tile-cache.md) §5.8 (the cache page is Settings › Tile layer, reached from the item); [ADR-101](ADR-101-studio-is-walked-the-way-a-portal-user-walks-it.md) §5.6 and §5.9 |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

ADR-101 shortened Studio's journeys and left its shape alone. The owner then named the shape as the
problem: the tile cache — a property of the whole service — was a page under each **layer**, reached by
clicking a layer name on the item's Overview; the same symbology editor had **four** doors (the item's
*Symbology* tab, which left the item; a button on every layer row; a link beside the Visualization map;
the layer page's own menu); and settings lived in three places — the item's tabs, each layer's pages, and
Server's service page — so where a setting was had to be known rather than seen.

The cause is recorded because it will recur: each time the symbology editor or the cache page was found
unreachable (commits 835b5dc, 2111c56), a door was added where the reader was, instead of moving the thing
to where it belongs. The doors accumulated; nothing was ever in the right place.

Portal's shape, as the reviewers cited it (doc.arcgis.com: *manage hosted feature layers*, *manage editing*,
*manage hosted tile layers*, *publish tiles from features*, *save layers*, *delete items*): an item page
whose **Settings** holds every setting of the item in sections — *Feature layer (hosted)*, *Tile layer
(hosted)* with *Build tiles / Rebuild cache* — and styling done in Map Viewer's Styles panel, saved to the
map or, with *Save layer*, to the layer's default.

## 2. Alternatives considered

### Alternative A — One item per face, as ArcGIS does (a feature layer item and a tile layer item)

**Argument for.** It is exactly Portal's model, and a Portal user's expectations transfer without
translation.

**Argument against.** ArcGIS splits them for reasons this server does not have: separate sharing, a cache
that must be rebuilt by hand, a separate delete. Here one service carries its feature, tile and map-image
faces under one sharing level, its cache empties itself on an edit, and its tile style is built from the
layers' own symbology. Two items would be two things to share and two to delete for one set of data.

### Alternative B — One item, four tabs, one home per setting (chosen)

**Argument for.** Every setting of the item is on the item; the tile cache is a section of its Settings
named as Portal names it, with a *Manage tiles* button on Overview; styling is one panel in Visualization.

**Argument against.** It reverses parts of ADR-034 §5c — settings a publisher owns but Server showed
move to Studio, and Server's layer pages go — and it is a large change to a console with 229 browser tests,
done in steps that must each leave the product working.

### Alternative C — Keep the structure, remove the duplicate doors

**Argument for.** Small, and it answers *"why is there a symbology button everywhere"* by the letter.

**Argument against.** It answers the question and not the complaint: the cache would still be under a
layer, and the next unreachable page would grow the next door.

## 3. Counterarguments to the preferred option

**The layer page was the owner's own routing, twice.** ADR-034 §5k made the row open the item and the
item's list open each layer, from the owner's screenshot of the reference. This keeps that — a layer
row still opens the layer — but *inside* the item (`?layer=`), as Portal's sublayer view is.

**Moving the symbology editor into a panel risks the owner's 1c design.** ADR-053's three columns become a
340-pixel panel. Its model, renderers, *Read the values* and the conversion's losses stay; what changes is
where it stands and what it is called.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Four doors to one editor | `SERVICE_TABS` symbology, Overview row button, `#visSymbology`, `LAYER_PAGES` | console.js, index.html (2026-09-30) |
| The cache page is per layer and controls the service | `LAYER_PAGES.caching` over `/admin/services/{name}/cache*` | console.js, AdminEndpoints.TileSeed.cs |
| Time field and visible range: publisher's privilege, Server's screen only | `content:publishFeatures` on both endpoints; drawn on Server's layer page | AdminEndpoints.cs, VisibleRange.cs |
| Delete of a layer or a service is admin-only in the API | `admin:manageAllContent` | AdminEndpoints.cs |
| Delete protection is not stored | a browser-side checkbox, re-ticked on every visit | console.js |
| Editor tracking cannot be undone | the endpoint adds four columns | HostedDataEndpoints.cs |

The reviews themselves are the evidence for the journeys: the item page scrolls sideways at 768 px, the
layer names vanish at 390, Settings is unreachable on a phone, and the caching page's *Pre-built* line
reported cleared levels as built.

## 5. Decision

5.1 **Tabs: Overview · Data · Visualization · Settings.** No Symbology tab. Viewers see three; owners and
administrators see Settings too. One **Layer** select in the item header for Overview, Data and
Visualization, bound to `?layer=`. The address holds `tab`, `layer`, `view`, `panel`, `section`, `mode` as
standing state (`replaceState`); a click on a layer row is a navigation, so Back returns to the item.

5.2 **One item for every face.** Overview says that features, vector tiles and the map image are one item
with one sharing level. My content matches a tileable item as both *Feature layer* and *Vector tile layer*.

5.3 **Settings: General · Feature layer · Tile layer** (the last only when the item has tiles). One save bar
for fields; buttons act at once. Each section ends with what the server administrator sets, read-only,
linking to Server for an administrator.

5.4 **One home per setting**:

| Setting or action | Home |
|---|---|
| Description; thumbnail redraw; URLs of every face; layer facts | Overview |
| Sharing and groups | the Share dialog only (Overview, My content row); stated once in Overview's details |
| CSV and GeoJSON download; VTPK and PMTiles packages | Overview › *Export data* *(and GeoPackage, zipped shapefile and Excel from 2026-10-01 — [ADR-107](ADR-107-a-layer-is-exported-as-geopackage-shapefile-or-workbook.md), the first step of ADR-106)* |
| Field names, hidden fields, add field; feature history log | Data › Fields; Data › History |
| Layer default style; visible range | Visualization › Styles, *Save as layer default*; Properties. *(Amended 2026-10-01 by [ADR-104](ADR-104-a-web-map-styles-its-own-layers.md): a map may also style a layer for itself, in the Map Viewer — a second level, not a second home for the default, which the map reaches through the same* Save as the layer's default.*)* |
| Delete protection (stored); Delete item | Settings › General |
| Editing operations; export allowed | Settings › Feature layer › Editing |
| Time field; history on/off; start or stop tracking who edits (stop since ADR-115); editors kept to their own; remove this layer | Settings › Feature layer › that layer |
| Cache status; Clear cached tiles; Pre-build an area | Settings › Tile layer |
| Who may download tiles | Settings › Tile layer › Offline use |
| Tile style override, named styles, sprite | Settings › Tile layer › Styles for vector tile clients |
| Tile lifetime and stale limit, one row per layer | Settings › Tile layer › Advanced |
| Tiling scheme, faces, SRID, page size, limits, timeouts, start/stop, refresh schema | Server › service page |
| Cache quota | Server › service page (condition 3; stays under Tile layer › Advanced until answered) |

5.5 **Doors.** Removed: the Symbology tab; Symbology, Data and Map buttons on Overview's layer rows;
`#visSymbology`; the `#/layer/<name>/<page>` screen on both surfaces, whose addresses redirect; the Settings
copy of sharing; per-layer Caching pages; Server's layer pages. Allowed, and only these: *Manage tiles* on
Overview; Data › History's empty state linking to turning history on; the Styles note linking to tile
styles when an override is stored; *Open the item in Studio* on Server and *Change these in Server* in Studio.

5.6 **Built in steps, each shipped on its own and none leaving the product worse:** 0 sidebar rail and
header reflow; 1 address state and one Layer select; 2 the redirect function with no rows active; 3
Visualization's Styles and visible range; 4 symbology doors removed; 5 Settings General and Feature layer;
6 Tile layer; 7 Overview and *Manage tiles*; 8 Export and Offline use; 9 Data Fields and History; 10 Server
loses layer pages; 11 the layer screen retired. **A redirect row is switched on in the same commit as the
page it points to**, and a setting leaves Server's layer page in the step it arrives in Studio — the
veteran's correction to the first plan, which would have left caching, fields and history unreachable
for several steps.

*(As built, 2026-10-01: all twelve steps, with two departures recorded rather than smoothed. **Step 3's
visible range** arrived in step 10, under the Style panel rather than in a Properties column of its own —
it was still on Server's layer page until then, so no setting was ever unreachable. **Step 10 did not
remove Server's layer page**: it removed every publisher setting from it — thumbnail, time column, visible
range, fields, history, maintenance — and left what is the server's about one layer: its state, contents,
identity, addresses and *forget the remembered shape*. Deleting that too would have moved server facts into
Studio, which §5.4's last rows say not to do. **Decided by the owner the same day (*"Hepsine evet"*): it became a section of Server's service page** —
*Layers*, one block per layer with its state, identity, addresses, *Show on map* and *forget the remembered
shape*; every Server `#/layer/…` address opens that section at that layer, so Server draws no layer page either. Step 11 is that Studio draws no layer screen: every `#/layer/…` address it is given opens the item,
and nothing in Studio links to one.)*

*(Three more, found by the ArcGIS reviewer walking the built item the same day:
**history on/off and start tracking who edits** are in Data › History and Data › Fields, where the layer page
had them, not in Settings › Feature layer as the table above says and as Portal puts *Keep track of who
created and updated features*; **offline packages** are built in Settings › Tile layer and only linked from
*Export data*, because they run as jobs; and Settings › General has its own *Change sharing…* button, which
opens the one Share dialog and so is a door rather than a second home. The first was a real departure and is **repaired the same day**: each hosted layer's *Keep history* and *Who creates and edits* are rows of its block in Settings › Feature layer, Data › History links there when history is off, and Data › Fields points there; the other two are recorded as chosen. The same review found Export data shown
to readers of a service whose owner had unticked *Export data* (Extract) — repaired: the owner may always
export, anybody else only when Extract is offered.)*

5.7 **Two differences between the reviewers, settled here.** Editor tracking is a one-way action per
layer, not an item-wide checkbox: the endpoint adds columns and cannot be undone, and a box that cannot
be unticked says something false. *(Amended 2026-10-01 by ADR-115: recording now stops as well as starts — the
columns stay — so it is a Start/Stop button per layer rather than a one-way one; still not an item-wide box.)* Visualization's *Features* mode says the server draws it and answers a
click with the feature's attributes.

## 6. Consequences

**Positive.** Where a setting is can be seen from the item. The owner's two examples — caching reached by
a button, symbology in the map — are the structure rather than exceptions to it.

**Negative.** Twelve steps of change across a console that is also a showcase; each step's tests are the
guard. ADR-053's editor loses its page. Server's layer pages, which administrators used, go.

- **State.** Delete protection becomes a stored column on `service` (step 5) — the one new piece of
  catalogue state; everything else moves presentation.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A Portal user is the reader Studio is measured against | `INFERRED` from the owner's requests of 2026-09-30 |

## 8. Dependencies

**Depends on:** [ADR-034](ADR-034-server-and-studio.md), [ADR-053](ADR-053-the-symbology-editor-is-three-columns.md),
[ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md), [ADR-093](ADR-093-seeding-the-tile-cache.md),
[ADR-101](ADR-101-studio-is-walked-the-way-a-portal-user-walks-it.md).

**Depended on by:** —

## 9. Revisit triggers

- ADR-056's item table is built: a title separate from the service name, tags, personal folders and a web
  map's own item page join this structure.
- One map engine is chosen for every map surface: the Styles panel moves onto it unchanged in place.

## 10. Conditions

1. **Owners choose their item's editing operations, within the administrator's ceiling?** Recommended yes
   by both reviewers; it needs the capabilities endpoint split so faces, tiling scheme, page size and SRID
   stay the administrator's. Until answered, Editing is shown read-only to an owner who is not an administrator.
   **DISCHARGED 2026-10-01 by owner decision — *"üçü de evet"*.** Migration 66 adds `service.editing_offered`, the
   owner's choice of Create, Update, Delete and Extract; what is served is the administrator's ceiling (every
   operation when it is null) narrowed to Query and that choice, computed where the catalogue is read, so the
   serving path is unchanged. `PUT /admin/services/{name}/editing` is the owner's or an administrator's; an
   operation the ceiling does not allow is refused by name. Settings › Feature layer offers the four, greys what the
   ceiling withholds and says so. Pinned by `ItemStewardshipConformanceTests`: withheld means neither advertised nor
   accepted.
2. **Owners delete their own item and layers, with delete protection enforced by the API?** Recommended
   yes. Today both deletes need `admin:manageAllContent`; until answered, the controls say so to an owner.
   **DISCHARGED 2026-10-01 by owner decision.** Deleting a service and unpublishing a layer are the owner's act or
   an administrator's (ADR-075's rule); migration 66 stores `service.delete_protected`, `PUT …/protection` sets it,
   and both delete routes answer 409 while it is on. **The default is off — confirmed by the owner 2026-10-01** (*"Hepsine evet"*, answering it among three questions; it was INFERRED until then): every script, fixture
   and test that deletes a service would start receiving 409 — a change to the API's answer nobody asked for — and
   Portal's own default is off. This softens the owner's earlier *"locked by default"* (2026-08), which was a
   browser checkbox re-ticked on every visit; the page now shows and changes the stored state.
3. **The cache quota needs `admin:manageServer`?** Recommended yes: it is spend. Until answered, it stays
   under Tile layer › Advanced with the privilege it has. **DISCHARGED 2026-10-01 by owner decision:**
   `PUT …/cache/quota` asks for `admin:manageServer`; Tile layer › Advanced shows the quota read-only to anybody
   else, with the sentence that it is the server's disk.

## 11. Dissent

None between the reviewers after §5.7. The design review's recommendation to replace My content's scope
strip, recorded in ADR-101 §11, still stands as dissent there.
