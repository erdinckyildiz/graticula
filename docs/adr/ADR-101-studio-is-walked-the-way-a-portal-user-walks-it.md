# ADR-101 — Studio is walked the way a Portal user walks it

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — every change below was reproduced in a browser before it was made and is pinned by a test that fails against the code it replaced; **what is inferred is where the changes stop**, and §5 labels each inferred choice for the owner |
| **Decided** | 2026-09-30, on the owner's instruction: *"uygulamanın arayüzü kullanılabilir değil. özellikle studio tarafını arcgis portal arayüzleriyle kıyaslayabilir misin? layer caching vs mantığı tamamen çağ dışı"*, and then *"ben uyurken beklemeden tüm bunları tek başına halledebilir misin? … ux designer'ın yorumları da önemli"*. The instruction is to fix; **which fixes, and where they stop, is this session's reading of it** and is marked `INFERRED` where it goes beyond a defect |
| **Amends** | [ADR-020](ADR-020-admin-console-and-service-status.md) §5c (`/console` lands in Studio, not Server); [ADR-034](ADR-034-server-and-studio.md) §6 (Studio's sidebar gains *Map*); [ADR-079](ADR-079-a-web-map-is-a-saved-document.md) §5 item 5 (saved maps are rows of *My content*, not a second table) and the viewer it describes (a vector tile layer is drawn in its own style; the operator's ground is offered); [ADR-093](ADR-093-seeding-the-tile-cache.md) §5 (how the Caching page presents a seed) |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

The owner, who uses the showcase daily from ArcGIS Pro and QGIS, said the console is not usable and asked for
Studio to be compared with ArcGIS Portal, and for the tile cache's logic, which they called outdated.

Four reviews were run on 2026-09-30 against a fresh fixture, and their findings were verified against the code
before anything was changed:

- **An ArcGIS administrator's review** of the code (it could not sign in to the showcase). Verdict: the
  back end is credible; Studio is not credible as a Portal replacement, and the root cause is that **there
  is no item** — the service is the item ([ADR-056](ADR-056-an-item-is-its-own-thing-and-a-service-is-one-kind.md)
  is accepted and unbuilt).
- **Three design reviews in a browser**: Studio's content journeys, the map viewers, and Server with its
  caching page. Each walked a journey step by step beside Portal's documented one and counted clicks.

What they found falls into three kinds, and this ADR treats them differently:

1. **Defects** — a control that does the wrong thing. Two were blockers: the Share dialog lost the chosen
   level on its way back from the group screen and saved `private` under a success toast; and *Owner* with
   a group ticked was saved as `private` plus a group row the server does not read, so members got nothing
   while the item's Settings page promised the opposite. Also: a saved map drifted about 10 % further out on
   every save and reopen; `view.html`'s *Map image* drew the Features face under a caption saying the server
   drew it; a link to a service's tiles arrived drawing features; a purged tile could still be served if its
   delete failed part-way (repaired separately in v1.0.211).
2. **Journeys that are longer or stranger than Portal's for no reason the design gives.** No *Map* in the
   navigation; search hidden until one page overflows; rows in case-sensitive path order; maps in a second
   table below every service; a new upload leaving the dialog open on an emptied form; a description the
   page asks for and nothing can write; a layer that opens on a page whose only content is *Delete*; the
   caching page leading with seconds, megabytes and level numbers.
3. **Structural gaps** — the item model, one map engine, one service page, pop-ups and labels in the map,
   tile-level invalidation, CSV upload and *Update data*. These are decisions, not repairs.

## 2. Alternatives considered

### Alternative A — Repair the defects only, and put the rest to the owner

**Argument for.** Nothing is decided by inference. The owner asked for a comparison, and the comparison is
the deliverable; every journey change is a product choice.

**Argument against.** The owner asked for it to be *handled*, while asleep, and said the design reviews
matter. Returning a list of thirty journey problems with two fixed is the answer they explicitly did not
want, and most of kind 2 has no second reading: a search box that is hidden when you look for it is not a
preference.

### Alternative B — Repair the defects and the journeys; leave the structure to decisions (chosen)

**Argument for.** Kind 2 changes are reversible, local, and each one is a place where Studio does
something Portal's user does not expect for no reason recorded anywhere. Kind 3 each needs a migration,
a new engine or a new ADR of its own, and each touches an earlier owner decision.

**Argument against.** The line between kind 2 and kind 3 is drawn here, not by the owner, and some kind 2
changes reverse things an earlier ADR chose on purpose (the maps' own table, `/console` → Server). §5 marks
each of those.

### Alternative C — Build the structure tonight too: the item table, MapLibre, one service page

**Argument for.** The veteran's verdict is that the item model is the root cause; everything else is
symptom. A night is long.

**Argument against.** ADR-056 moves ownership and sharing onto a new table and backfills every service —
a migration on the showcase, which the owner's own rule says is rehearsed on a copy and reported, not
slipped in overnight. MapLibre replaces ADR-079's engine and needs the benchmark the design review itself
asked for. One service page reverses ADR-034 §5c's split, which was the owner's. Doing any of them by
inference is what [CLAUDE.md](../../CLAUDE.md) §2 forbids.

## 3. Counterarguments to the preferred option

**Moving `/console` to Studio reverses a rule the project took on purpose.** ADR-020 §5c made frozen URLs
a rule, and a bookmark to `/console` has taken administrators to Server since the surfaces split. The
answer is that the *address* still works; what changes is where it lands, and the first thing Server
showed an administrator on most servers was *0 services — nothing in the root*. Still, it is the owner's
product and §5.1 is `INFERRED`.

**Merging maps into the content list undoes ADR-079 §5 item 5's stated reason.** That section kept maps apart
because four of seven cells would be blank. With the table at five columns a map fills all five, which is
the reason gone rather than overruled; but the decision was taken with that reason and this changes it.

**Hiding the `hosted` schema from *Publish a table* removes something an operator could do.** They could
republish a hosted layer's storage as a registered layer. Nobody means to, and `tools/ci-free-tables.sql`
already records that `hosted` is the server's alone (ADR-058), so this makes the screen agree with a
decision rather than take one.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The Share dialog saved `private` after *Organization* was chosen | Reproduced in a browser; `ShareDialogTests` fails against the previous `console.js` | design review 2026-09-30, `tests/Graticula.Console.Tests/ShareDialogTests.cs` |
| Group membership is read only for the `group` scope | `LayerAccess.Evaluate`, `GroupConfersEditing` | `src/Graticula.Platform/Identity/LayerAccess.cs` |
| Saved maps drifted ~10 % per save | zoom 13.77 → 13.63 → 13.49 over three rounds, from a 40 px fit margin on restore | design review, `webmap.js` `wmFit` |
| Vector tile layers drew in one palette colour | `wmPlainStyle` was the only style the tile branch had | `webmap.js` before this change |
| Search hidden until a scope passed ten rows | `contentFilter.hidden = inScope.length <= PAGE_SIZE` | `console.js` before this change |
| The description column had no writer after publishing | only the composer's `insert … on conflict` sets it | `PostgresAdminCatalog.cs` |
| All new tests fail against the code they replace | control runs 2026-09-30: 2/2 share, 4/4 content list | this session |

Portal's side is taken from its public documentation — *item details*, *search* (the Content page),
*add files as items*, *share items*, *Map Viewer*, *manage hosted tile layers* at doc.arcgis.com — as
cited in the review reports; nothing here reproduces Esri code or behaviour beyond what those pages
describe.

## 5. Decision

**Repair every defect found, shorten every journey whose extra steps no decision explains, and take
none of the structural decisions.** Specifically:

5.1 **`/console` lands in Studio's content list** (`302`, not permanent). An administrator switches to
Server; a publisher is no longer greeted by a refusal naming a privilege identifier. `INFERRED`.

5.2 **Studio's sidebar has *Map***, a link out to the web map viewer. It is not a routed tab — a tab is an
address this app routes (D-115) — so it is drawn after *My content* from `SURFACES.studio.links`.

5.3 **My content** keeps the owner's scope strip (2026-08-18) and gains: a search box always shown; a type
filter over the types present; an order, *newest first* by default; the bare name as the title with the
folder in the line under it; *Where* and *Status* columns folded into that line; **saved maps as rows** of
the same list (amends ADR-079 §5 item 5). Data is read once and redrawn on every keystroke, instead of asked
for again. `INFERRED` for the merge of maps.

5.4 **A successful upload closes the dialog and opens the new item's page**, with a toast that says it is
private until shared. The coordinate-system note says what is true: a `.prj` is read.

5.5 ***Publish a table this server can reach* offers no table in the `hosted` schema**, and fills in the
object-id column the probe found (still editable; Q-57's *nomination* stands). `INFERRED` for the prefill.

5.6 **The item page** gains *Open in Map Viewer* and *Share* beside its picture; a description written
there, stored by a new `PUT /admin/services/{name}/description` (the owner's or an administrator's act,
ADR-075; at most 4,000 characters; audited as `service.describe`); dates in the reader's locale; the
sharing level shown once. A layer opens on its first page that is about the layer — *Symbology* — rather
than on *Maintenance*. **The Data table** shows every column, fifty rows a page with the total, orders
by a column when its name is pressed, and exports the layer as CSV or GeoJSON — read in pages through the
layer's own query, so nothing the reader may not read leaves the server, and stopped at 100,000 rows.

5.7 **The Share dialog** keeps the level chosen across the group screen, confirms groups with *Done*, and
saves *Owner* with groups ticked as the `group` scope — groups added first, then the level, then removals,
since the server refuses a group-scoped item with no group. **Whether group sharing should instead be
additive on the server, as Portal's is, is not decided here** (condition 2).

5.8 **Server's services**: a search reads every folder; the first landing opens the folder with the most
services when the root holds none; the row's actions stay on screen at 1280 px.

5.9 **The Caching page leads with what the cache does** — *built the first time somebody views an area,
cleared as soon as its data changes through this server* — what is cached now, and a **Clear cached
tiles** control backed by a new `POST /admin/services/{name}/cache/clear` (the seed's privilege and
ownership rule; audited as `service.cache.clear`). Pre-building is expressed as an area and two scales
(*1:36k — town (level 14)*), with the area taken from a map on the page itself rather than from the
Visualization tab's. The lifetime, stale limit, quota, budget and per-level read-back are under a closed
*Advanced*. An export whose package holds no tile says so instead of *0 · done* beside a 4 MB file.
`INFERRED` for what is under *Advanced*.

5.10 **The web map viewer** draws a vector tile layer in its service's own style (fill, line and circle
layers of the style this server writes, with its legacy filters); restores a saved view without the
framing margin; offers the operator's map ground (ADR-086) as a basemap and starts a new map on it; asks
for a name on a new map's first save; shows a signed-out reader of a shared map no editing controls.
**`view.html`** draws a named layer on the MapServer face as a map image of that layer. **Visualization**
keeps a requested *tiles* mode until it knows whether the service has tiles.

## 6. Consequences

**Positive.** Every defect the reviews reproduced is repaired and pinned. A Portal user's first ten
minutes — sign in, find content, add a layer, open it on a map, share it, write what it is — now go the
way they expect, and the caching page says the thing that is true about this engine before anything else.

**Negative.**

- **The root cause is untouched.** There is still no item: no tags, no personal folders, no custom
  thumbnail, no item page for a map. Every list improvement above is a better view of the wrong model.
- **Two routes are new** and are ArcGIS-shaped only in spirit; neither is part of any public API.
- **The web map's style interpreter is a subset.** Symbol layers — labels and icons — are not drawn, and
  a filter it does not understand lets the feature through. It is the repair that makes today's viewer
  honest, not the engine decision.
- **State.** One column written that was only written at publish (`service.description`); no new table, no
  runtime state. *Clear cached tiles* removes files the cache already owns.
- **Three places still draw maps three ways** (the SDK on Visualization, OpenLayers in the viewer, server
  pictures in previews). They now agree more; they are not one component.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | A Portal user is the reader Studio's journeys are measured against | `INFERRED` from the owner's request to compare with Portal |

## 8. Dependencies

**Depends on:** [ADR-020](ADR-020-admin-console-and-service-status.md),
[ADR-034](ADR-034-server-and-studio.md), [ADR-036](ADR-036-groups.md), [ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md),
[ADR-079](ADR-079-a-web-map-is-a-saved-document.md), [ADR-086](ADR-086-the-operator-chooses-the-map-ground.md),
[ADR-093](ADR-093-seeding-the-tile-cache.md).

**Depended on by:** —

## 9. Revisit triggers

- ADR-056's item table is built: My content, the item page and the maps' rows are redrawn from it.
- A map engine is chosen for all map surfaces: §5.10's interpreter is deleted.
- The owner decides group sharing is additive on the server: §5.7's save rule becomes *keep the level*.

## 10. Conditions

1. **The owner confirms or reverses each `INFERRED` choice** — §5.1 (Studio as the landing), §5.3 (maps in
   the content list), §5.5 (identity prefill), §5.9 (what sits under *Advanced*).
2. **Group sharing: additive on the server, or the `group` scope as now.** Portal's is additive — sharing
   with a group adds those members to whatever level is set, including editing through a shared-update
   group under *Organization*. This server reads groups only under the `group` scope. §5.7 makes the
   dialog honest about the current rule; it does not choose between them.
3. **Tile freshness in browsers (V-56, [ADR-069](ADR-069-query-responses-carry-the-layer-s-cache-lifetime.md)).** A layer anybody can edit is served
   `no-cache` and revalidated with an ETag on every view, by the owner's decision of 2026-09-23. The
   alternative the reviews raised is a data version in the tile URL with long-lived, immutable
   responses. Not changed here; put to the owner.
4. **The structural decisions** — the item model (ADR-056), one map engine with Styles, Pop-ups and
   Labels panels (the design review proposed MapLibre drawing the served style), one service page across
   Server and Studio (reverses ADR-034 §5c), tile-level invalidation instead of whole-layer purge, CSV and
   XLSX upload, *Update data / Overwrite / Append*, and editing in the Data table — are each
   the subject of their own decision and are listed here so that none of them is mistaken for done.

## 11. Dissent

**The Studio design review recommended replacing the scope strip** (Everything / Mine / From my groups /
Organization / Public) with Portal's *My content / My groups / My organization*, because *Public 0* sits
above rows that each show a *public* pill. **Declined here**: the strip is the owner's own request of
2026-08-18, and the counts mean *how it reached you*, which the strip's notes say. The reviewer's reading —
that a Portal user will take it as broken data — is recorded as the case for revisiting it.
