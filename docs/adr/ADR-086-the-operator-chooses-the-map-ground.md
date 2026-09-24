# ADR-086 — The operator chooses the map ground, and it is the portal's default basemap

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-09-24, by owner decision. [Q-110](../open-questions.md) was answered on 2026-09-23 — *"Operatörün kendi OSM verisi"* — and the follow-up put to the owner, whether that means the server draws the operator's layers automatically or the operator picks them, was answered *"Yönetici seçer, yoksa OSM karoları kalır"*: the operator chooses, and with nothing chosen OpenStreetMap's tiles stay. Asked whether the showcase should be set to its Turkish layers, the owner answered *"Evet, seçilsin"*. |
| **Supersedes** | — (amends [ADR-020](ADR-020-admin-console-and-service-status.md) §4c, whose OpenStreetMap default becomes the fallback) |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Every map this server draws — the console's map panel, the SDK page, the OpenLayers viewer — has had
OpenStreetMap's public tiles under it since [ADR-020](ADR-020-admin-console-and-service-status.md) §4c, with a
per-browser choice of this server's own tile services in the viewer. ADR-020 §4d showed an operator's OSM
extract imported and served as this server's own tiles; nothing let an operator make that the ground for
everybody. [Q-110](../open-questions.md) asked what the ground should be, and the owner answered.

## 2. Alternatives considered

### Alternative A — a server setting, served as the portal's default basemap *(chosen)*

**Argument for.** ArcGIS keeps an organisation's default basemap in `portals/self.defaultBasemap`, and Map
Viewer and the Maps SDK read it there. Putting the operator's choice in the same place means one Save changes
the ground in this server's pages and in ArcGIS clients alike, and the pages need no endpoint of their own.

**Argument against.** `portals/self` grows a store read.

### Alternative B — draw every imported layer that looks like a ground

**Argument for.** Nothing to configure.

**Argument against.** Deciding that a layer is a ground from its name is a convention nobody agreed to; the
viewer's comments already refused it once. Offered as the reading of *"operatörün kendi OSM verisi"* and not
chosen.

### Alternative C — a separate endpoint for the console

**Argument for.** Smaller than the portal document.

**Argument against.** A second place saying what the ground is, which ArcGIS clients would not read.

## 3. Counterarguments to the preferred option

- **A ground shared with a group is not everybody's.** `portals/self` names only the services the caller may
  draw, so a caller who may not see one is given the rest, or OpenStreetMap. The settings screen says which
  services not everybody will see.
- **The pages read `portals/self` anonymously**, so a ground shared only with a group is not drawn in the
  console even for that group's members. Deliberate: a tile request from the map is anonymous too, and a
  ground the page is told about has to be one it can draw.

## 4. Evidence

- `TheServerHasOneGroundTests` (conformance): a public and a private tile service chosen; the administrator's
  `portals/self` names both in the chosen order, an anonymous caller's names only the public one, a name that
  is not a tile service is refused with the ground unchanged, and clearing gives OpenStreetMap.
- `TheServersGroundIsDrawnTests` (console): with a ground stored, `map.html` and `view.html` draw it and say it
  is the server's, the viewer without OpenStreetMap under it, and a browser that chose for itself keeps its
  choice. **Controlled**: with `ground.js` ignoring the server's ground, the test fails on `map.html`'s caption.
- `SettingsScreenTests` (console): the drawing order, moves, and a private service offered with its caution.

## 5. Decision

5.1 **The ground is a server setting, `map_ground` in `server_setting`**
([ADR-084](ADR-084-the-page-size-is-one-number.md)'s table): a list of this server's services, bottom first,
at most eight. Set by `PUT /admin/settings/ground` with `admin:manageServer`; read with the page size by
`GET /admin/settings`, each service marked with whether it still exists and whether everybody may draw it.

5.2 **Each name must be a service that serves vector tiles when it is saved**; a name with no folder is looked
for in `hosted` too. Sharing is not required: a ground for a group is a legitimate choice.

5.3 **`portals/self.defaultBasemap` carries it**, as `VectorTileLayer` entries naming only what the caller may
draw — sharing, running, tiles on. With nothing left it is OpenStreetMap, as `layerType: "OpenStreetMap"`.

5.4 **Order of precedence in the pages** — `ground.js` `groundTilesToDraw`: the browser's own choice, when it
made one (an empty list is a choice); else the server's ground; else OpenStreetMap's tiles; else Natural Earth.
The console's map panel and `map.html` wait for the portal before building the map; the viewer adds the ground
when the portal answers.

5.5 **Chosen on the Settings screen**, in a *Map ground* card: this server's vector tile services as ticks in
an alphabetical list that never moves, and the chosen ones as a drawing order, top first, moved with ↑ and ↓.
The candidates come from `GET /admin/settings` — every service with tiles on, with its sharing and whether it
runs — so a private service is offered and its caution shows before it is ticked.

**Revised after the design review of 2026-09-24**, which found the first card taking its list from the
anonymous services directory (so the private service an operator had just published was missing, publishing
being private by default), making the order of ticking the order of drawing while showing a different order,
calling a stopped service unshared, and telling the operator that people who cannot see one service get
OpenStreetMap when they get the rest of the ground. It also found the viewer drawing the server's ground over
OpenStreetMap while the other pages drew it instead, with the ground's buttons shown off; the viewer now draws
it instead, shows it pressed, starts a personal choice from it, and offers *Use the server's ground* back.

## 6. Consequences

**Positive.** A deployment with an imported extract draws it under every map without anybody configuring a
browser; ArcGIS clients see the same ground.

**Negative.** Every page load reads one setting more.

**Ports created.** None.

**State.** One row in `server_setting` (`map_ground`), JSON array of qualified service names. No migration: the
table is ADR-084's.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| — | ArcGIS clients read a portal's default basemap from `portals/self.defaultBasemap` | Esri's documented portal self response; not yet observed against this server — condition 2 |

## 8. Dependencies

**Depends on:** [ADR-020](ADR-020-admin-console-and-service-status.md) §4c–§4d,
[ADR-084](ADR-084-the-page-size-is-one-number.md) (the settings table and screen).

**Depended on by:** —

## 9. Revisit triggers

- An operator whose ground is shared with a group, and whose group members expect to see it in the console.
- A request for a raster tile service as the ground.

## 10. Dissent

None recorded.

## 11. Conditions

1. **The showcase's ground is set to its Turkish layers** — `tr_kara`, `tr_il`, `tr_ilce`, `tr_yol`, `tr_yer`,
   the owner's *"Evet, seçilsin"*. Needs the showcase's administrator, which this session does not have.
2. **An ArcGIS client is seen drawing it** — Map Viewer, or the Maps SDK with the portal set, opening a new map
   on this server's ground.
3. **The web map editor starts a new map on it.** `webmap.js` draws OpenStreetMap under every web map, and a new
   web map in ArcGIS starts from the portal's default basemap; it does not yet.
