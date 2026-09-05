# ADR-057 — Composing and publishing a service

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` for the composition and the publish rules · `MEDIUM` for the faces |
| **Decided** | 2026-09-05, by owner decision |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

A table imported into the built-in datastore becomes a service on the way in. A table in a
*registered* database becomes nothing: it can be probed, it can be listed, and there is no
screen anywhere that turns it into something a client can open. That is the whole of what
this decision is about — the owner, 2026-09-05: *diğer databaselerden eklediğimiz servisleri
publish edemiyoruz.*

The shape was named rather than invented. The owner sent ArcGIS Pro's window: a Contents
pane on the left, a Catalog on the right, a map between them, and *Share As Web Layer* over
it. A screen study was built against that shape and against
[Pro's own documentation](https://pro.arcgis.com/en/pro-app/latest/help/mapping/map-authoring/contents-pane.htm),
and this ADR records what the study settled.

What it does **not** cover: the item record every published thing gets, which is
[ADR-056](ADR-056-an-item-is-its-own-thing-and-a-service-is-one-kind.md), and the connection
dialog the databases come from, which is
[ADR-055](ADR-055-a-connection-is-fields-and-the-server-assembles-it.md).

## 2. Alternatives considered

### Alternative A — Publish one layer at a time, as the datastore import does

**Argument for.** It already exists. `POST /admin/layers` takes a source, a table and a name
and produces a layer; a screen over it would be a form, not a composition, and there would be
nothing new below it at all.

**Argument against.** It cannot express a service, and a service is what ArcGIS clients open.
*A service is a combination of layers* — the owner's own correction, 2026-08-15 — and layer
order, grouping and a shared coordinate system are properties of the combination. Publishing
one at a time means the operator assembles the thing they wanted by repeating a form and
hoping the order came out right.

### Alternative B — Compose, then publish

**Argument for.** The composition is the unit the operator is thinking in and the unit the
client receives. Order, grouping, symbology and the served reference are decided against a
picture of the whole thing, and *Publish* is one act with one result.

**Argument against.** It needs a screen with three panes, drag and drop, and a preview — and
below it a catalogue that can hold a group and a chosen coordinate system, neither of which
it can today.

### Alternative C — Compose in ArcGIS Pro and publish to this server

**Argument for.** Pro is where the owner's users already are, and it already does all of
this.

**Argument against.** Publishing from Pro is a SOAP handshake this server does not implement
and does not plan to — the front page says so. It would also make composing a service require
a licence for somebody else's product, which is the opposite of what a source-available
server is for.

## 3. Counterarguments to the preferred option

**Group layers are a migration for a convenience.** They are a migration. They are not a
convenience: a service with twenty layers and no grouping is a legend nobody can read, and
the alternative operators actually take is publishing four services instead of one, which
moves the problem into the directory.

**The served coordinate system will be set wrong and nobody will notice.** The dialog names
each layer's stored reference beside the served one, so a reprojection is visible before it
costs anything. What makes this safe rather than hopeful is that the machinery is already
proven: `outSrid` and `filterSrid` go on every query the drawing path makes, and
[D-226](../architecture-debt.md) is the row where drawing in the caller's reference was
measured to the half-pixel.

**Blocking a duplicate name while somebody types is a lot of round trips.** It is one request
per pause in typing against a listing this server already serves. The alternative is finding
out at the end of a composition that took five minutes.

## 4. Evidence

Measured or read on 2026-09-05.

| | |
|---|---|
| Ways to publish a registered table today | **0** |
| Migration that built group layers | **12** — table, parent column, foreign key, counter |
| Console controls that make a group | **1** — the *New service* drawer, corrected same day |
| Column holding a service's served reference | **none** |
| Face flags in the catalogue | **2** — `ServesFeatures`, `ServesTiles` |
| Faces the screen offers | **4** — Feature, Map, VectorTile, OGC |
| Capability ceiling the catalogue holds | `Query`, `Create`, `Update`, `Delete` |
| Capabilities the ArcGIS REST specification defines | 8, including `Editing`, `Sync`, `Uploads`, `Extract` |
| Tag column on a service | **none** |

The map face is derived from *does this service have geometry* and the OGC faces follow it,
so two of the four switches have nothing behind them yet. That gap is why the confidence on
the faces is `MEDIUM` and it is §7's second condition.

## 5. Decision

### 5a. The composition is the service

The Contents tree's root **is** the service being published: its name is the service name,
its order is the layer order, and index 0 is drawn on top. It is called `Map` until renamed,
which is Pro's default and the owner's request.

### 5b. Groups are one level deep, and the catalogue already holds them

A layer may sit in a group; a group may not sit in a group. Dragging a group into a group
contributes its layers rather than itself.

~~The catalogue gains a parent on the layer row, and the ArcGIS face answers with
`type: "Group Layer"` and its `subLayerIds`.~~ **Corrected 2026-09-05, on reading the
schema: all of that exists.** Migration **12** created `group_layer`, put
`parent_layer_index` on `layer` with a foreign key saying a parent must be a group, gave
`service` a `next_layer_index` counter, and allowed a group inside a group. The face emits
`type: "Group Layer"`, `parentLayerId` and `subLayerIds`, and
`POST /admin/services/{name}/groups` creates one.

**So there is no migration here and the decision is narrower than it read.** ~~What is missing
is a way to make a group *from a screen* — nothing in the console offers one —~~ **wrong twice
in one paragraph, corrected 2026-09-05 when the owner sent a picture of the screen that does
it.** Server's *New service* drawer creates an empty service and then a group layer inside one.
What is missing is not the control but the *shape* of it: that drawer asks for a container
first, then a group, then a layer index to nest under, which is this server's API in the order
the API wants and not the order a person works in. The Publish screen is the replacement, and
this drawer is what it retires — and the depth
limit, which is a rule this screen keeps rather than a constraint the database enforces.
The schema is more permissive than the decision; that is the right way round, and the day
somebody wants real nesting it is a screen change and not a migration.

### 5c. The coordinate system belongs to the service

One reference is chosen at compose time and everything is served in it. A layer stored in
another is reprojected per feature by PostGIS on the way out, which is what the drawing path
already does for every request in another reference.

### 5d. A folder is chosen or created by naming it

The folder box lists what exists and accepts a name that does not; publishing creates it.
That is Pro's behaviour and it removes a separate *create folder* act nobody would find.

### 5e. A service name is unique within its folder, and only its owner may replace it

**By owner decision.** Two folders may each hold a `parsel`; one folder may not hold two.
The name is checked **while it is typed**, against the folder chosen, so a collision is
refused where it is made rather than after a composition is finished.

A name already taken by **somebody else** is refused outright. A name already taken by
**you** is offered as a replacement, naming what is there — its layer count and when it was
published — so overwriting is a decision made against the thing being overwritten.

### 5f. A published service is running

Publishing means serving: the URLs answer immediately. There is no draft state and no second
act to remember. A service can be stopped afterwards, which is what the status is for.

### 5g. The faces are chosen at publish, and so is the ceiling

Feature, Map, VectorTile and OGC are switches. Under the feature face, the capability
ceiling — `Query`, `Create`, `Update`, `Delete` — is chosen; `Query` cannot be unset from
this screen, because a service that answers nothing is a state
[ADR-031](ADR-031-service-capability-configuration.md) reaches by stopping it rather than by publishing it.

`Editing` is **derived, never offered**: the REST specification says it appears when any of
Create, Delete or Update is enabled, so the screen shows the resulting string rather than a
switch that can disagree with the server. `Sync`, `Uploads` and `Extract` are in the
specification and not in this server, so they are not drawn —
[ADR-034](ADR-034-server-and-studio.md)'s rule.

### 5h. A service is not created without layers

**By owner decision, 2026-09-06, asked directly whether the Publish screen should keep a home
for the empty container the current drawer offers: *hayır. katmansız servis yaratılamaz.***

Publishing creates the service **and its layers in one act**. There is no empty-container step
and no screen that offers one, which removes the three-act sequence the *New service* drawer
teaches — *create the service, add the groups, then publish layers into it naming the group to
nest under* — and with it the layer index nobody can find.

**Created is not the same as exists, and conflating them would break two things that work.**
A service loses its last layer when somebody unpublishes one; that state is real, it is
already handled, and the handling is load-bearing:

- `PostgresLayerCatalog` joins layers with a **left** join precisely so a service with none is
  still listed — the comment says why, and it is about an administrator who would otherwise
  conclude that creation failed.
- `GET /admin/featureservices/empty` and `POST /admin/featureservices/sweep` exist to find and
  remove exactly this residue. The product already treats an empty service as something that
  **happens** and is cleaned up.

So this decision narrows the *creating* end and leaves the *existing* end alone. An empty
service remains visible, listable and sweepable; it is simply no longer something a person can
make on purpose.

**What it costs.** `POST /admin/featureservices` creates one today and is the only way to make
a service at all, so it cannot be refused before the Publish path can create service and layers
together. The order is: Publish first, then the endpoint requires layers, then the drawer's
*Create empty service* comes off the screen. Doing it in any other order leaves this server
with no way to publish anything.

## 6. Consequences

- ~~**Two catalogue changes before the screen is worth building**: a parent on the layer row
  (5b) and a served reference on the service (5c). Both are migrations.~~ **One**, and it is
  5c. Grouping was built in migration 12 and is reachable at
  `POST /admin/services/{name}/groups`; what it has never had is a control. Corrected before
  either was written, which is the only useful moment to correct a cost.
- **Two more if the faces are to mean anything.** `MapServer` and the OGC faces are derived
  today; making them switchable is two columns beside `ServesFeatures` and `ServesTiles`.
- **The uniqueness rule needs a listing the dialog can ask** — folder plus name plus owner —
  and it is a read of what `/content/items` already returns.
- **State.** A parent column on the layer row, a reference column on the service row, and
  two face flags. No new table: the item every publish creates is
  [ADR-056](ADR-056-an-item-is-its-own-thing-and-a-service-is-one-kind.md)'s and is counted
  there rather than twice.
- **Publishing writes the service and its item in one transaction**, per ADR-056 — a service
  with no item is invisible in Studio.
- **And its layers in the same one**, per 5h. A half-published service — a container whose
  layers failed — is the empty residue this decision refuses to create deliberately, so it must
  not be created by accident either.
- **Tags are asked for nowhere**, because a service has no tag column. On an item they are
  obvious, which is ADR-056's territory rather than this one's.

## 7. Conditions

1. **The name check is measured against a folder with a thousand services in it.** 5e asks
   for a request while somebody types; whether that is one query or a listing walked in the
   browser decides whether the screen is usable on a full server, and nobody has looked.
2. **The faces become flags, or the two undrawable switches come off the screen.** MapServer
   and OGC are drawn as choices and are not choices yet. Either the catalogue gains the two
   columns or the screen stops offering what it cannot deliver, and shipping it in between is
   ADR-034's prohibition with extra steps.
3. **`POST /admin/featureservices` requires layers, and the drawer's *Create empty service*
   comes off the screen.** 5h decides this and cannot be applied yet: that endpoint is the only
   way to make a service until the Publish path exists. The condition is here so the sequence
   is not forgotten, because the intermediate state — a rule the screen keeps and the API does
   not — is the shape ADR-033 warned about, where the next writer bypasses it.
4. **A service with a group is opened by a real ArcGIS client.** `type: "Group Layer"` and
   `subLayerIds` are what the specification says; what Pro and the JavaScript API actually do
   with a group layer served by something that is not ArcGIS Server is not known here, and
   the first person to find out should not be the owner.
