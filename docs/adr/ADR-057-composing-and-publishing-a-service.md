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
| Ways to publish a registered table today | ~~**0**~~ — `POST /admin/publish`, 2026-09-06 |
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

~~Feature, Map, VectorTile and OGC are switches.~~ **Corrected before it was built, 2026-09-06:
two of the four are.** `serves_features` and `serves_tiles` are columns on the service row and
every read face honours them — the tile URL answers 404 with the second off, and the feature,
WMS, WFS and OGC faces all check the first. **MapServer and the OGC faces have no column and no
endpoint**: they are derived from the feature face, so a switch for them would be a control for a
capability that does not exist, which is [ADR-034](ADR-034-server-and-studio.md)'s prohibition.
The screen names them and says they follow, because *not drawn and not explained* leaves an
operator wondering whether the dialog forgot them.

Under the feature face, the capability ceiling — `Query`, `Create`, `Update`, `Delete` — is
chosen; `Query` cannot be unset from this screen, because a service that answers nothing is a
state [ADR-031](ADR-031-service-capability-configuration.md) reaches by stopping it rather than by
publishing it. **The ceiling narrows and never grants**, and the screen says so in the sentence
under the boxes: what a caller gets is this intersected with their privileges and with what the
data supports.

~~`Editing` is **derived, never offered**: the REST specification says it appears when any of
Create, Delete or Update is enabled, so the screen shows the resulting string rather than a switch
that can disagree with the server.~~ **Measured 2026-09-06 and the claim was about ArcGIS rather
than about this server: `Editing` is never emitted here at all.** `PrivilegedCapabilities` builds
`Query`, `Create`, `Update`, `Delete` and nothing else, so the string a client reads never carries
it. Not offering it as a switch is still right; describing the screen as showing a derived
`Editing` was describing a document this server does not write. Whether an Esri client needs that
token to offer editing is a conformance question this decision does not answer and
[Q-145](../open-questions.md) now asks.

`Sync`, `Uploads` and `Extract` are not drawn — the first two are not in this server at all, and
`Extract` is in `ServiceCapabilityLimits.Known` but is never granted by
`PrivilegedCapabilities`, so a ceiling containing it could only ever narrow to nothing.

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

**All three are done — 2026-09-06.** `POST /admin/publish` writes a service, its groups and
its layers in one transaction and Server's **Publish** tab composes one; the endpoint answers
400 naming its replacement; the drawer keeps only its group form. §7 condition 4 carries what
the last two cost and what they found.

### 5i. One table is one layer *within a service* — and the schema's answer was not a decision

~~**One table is one layer, and the schema said so before this decision did.**~~ **Overturned by
the owner the same day it was written, 2026-09-06:** *"bir tablonun bir serviste kullanılması,
başka bir serviste kullanılmasını engellemez. in use durumu saçma."*

**Found 2026-09-06, by publishing a composition that named one table twice.** The refusal
came back saying the *service name* was taken, which was this endpoint mistranslating a
constraint it had not expected. The constraint was `layer_table_unique` on
`(data_source_id, schema_name, table_name, geometry_column)`, and it was **global** — not per
service.

~~So *the same feature class twice with different filters*, which this ADR had left open as a
question for the owner, is already answered, and more strictly than the question assumed: not
once per service, once per server. A second view of the same data is a database view, and the
composition names that instead.~~

**That paragraph is the mistake worth keeping visible, and it is a particular kind.** An open
question was closed by finding that the schema already forbade the thing — and the schema
forbade it by accident. `layer_table_unique` arrived in **migration 1**, in the `create table
layer` statement, with a long comment above it about identity columns and geometry types and
not one word about this. Nobody decided it. Reading a constraint as a decision is how an
implementation detail becomes a product rule without anybody agreeing to it, and the register
has a name for the reverse of this — [ADR-034](ADR-034-server-and-studio.md), a control drawn
for a feature that does not exist. This was a rule enforced for a decision nobody took.

**What it cost was visible on the screen.** The Publish screen read `/admin/layers`, struck
through every table any service already served, and refused the drag — on a developer's
database that is most of the list. The owner saw it and said so.

**The rule now:** a table may be published into as many services as anybody likes. Migration 40
drops the global constraint and replaces it with `layer_table_unique_in_service` on
`(service_id, …)`, so `parsel` is servable from the cadastre service and the planning service at
once. The console greys nothing, and the request that fetched the whole layer list to grey
things out is gone with it.

**One question is left open rather than answered by a constraint again.** The same table twice
*inside one service* is still refused. That is the *two filters on one feature class* case, and
filters do not exist on a composition yet — so the refusal costs nothing today and the decision
can be made when there is something to decide. It is stated here so that the next reader does
not find the index and conclude, as this section once did, that somebody chose it.

**The endpoint translates each constraint rather than assuming there is one.** A publish can
collide four ways — the service name, the table, a layer name inside the service, and an index
this server allocates — and telling somebody their *name* is taken when their *table* is
published sends them to rename something that was fine. The unmatched case prints the
constraint, which is how the first mismatch was found in one request: the service index is
called `service_name_in_folder_ci`, because a later migration made it case-insensitive and
renamed it, and an exact-name match had quietly fallen through.

### 5j. The composition is drawn before it exists

**By owner decision, 2026-09-06**, when the built screen was put beside the design study it came
from and the question was whether they were the same screen: *"db'den okuduğunu direkt çizebilen
bir yapı olmalı. db bağlantısı varsa çizebilmeli de. gerçek önizleme ile benzer bir yapı."*

`POST /admin/publish/preview` takes the composition, reads the tables out of the databases it
names, and returns a PNG. **Nothing is published to draw it**, and the conformance test asserts
that as its second fact: a preview implemented by publishing to a hidden service would pass the
first assertion for months and leave a service behind per look.

**It is the real drawing path, not a second one.** The loop is `MapServer/export`'s loop — the
same `MapRenderer`, the same `WmsEndpoints.DrawLayerAsync`, the same symbology, the same
reprojection. A preview with its own renderer would be a picture of that renderer.

**What made it cheap was measured before it was written, and it could as easily have been
false.** `LayerConnections.SourceFor` reads three things off a `PublishedLayer` — its connection
string, its definition, its statement timeout — and never asks the catalogue whether that layer
exists. So a layer assembled in memory from a composition entry reads features exactly as a
served one does. Had that not held, the preview would have needed a temporary service and a
decision of its own.

**Three things travel beside the image**, because a picture cannot say them: the reference in
`X-Graticula-Srid`, the frame in `X-Graticula-Extent`, and — on the screen — the layer indices,
which is what a client asks for and what the drawing replaced when it took the summary's place.

**The record ceiling is lower than a served map's**, and that is a bound rather than a promise:
a preview is looked at while somebody is still deciding, so it is answered quickly or it is not
looked at.

**And the drawing sits on a map — owner instruction the same day:** *"preview kısmında bir
harita olsun. nothing to draw yet yazmasın."* The pane held a sentence saying there was nothing
to show; a ground answers *where am I* without being read, and an empty composition is then an
empty map rather than an explanation. The ground is OpenLayers over OpenStreetMap, the same one
the symbology editor's preview stands on, loaded from this origin because `script-src 'self'`
admits nothing else.

**Which makes the reference two different questions, and only one of them is `Served in`.** The
ground is Web Mercator, so the picture is drawn in Web Mercator — a composition somebody chose
to serve in 4326 would otherwise line up with nothing. `Served in` decides what the *service*
serves, and the tree's reprojection marks are where that choice is visible. So the endpoint
takes `bboxSR`, which the symbology preview learnt the hard way: four numbers with no reference
were read as the layer's own, every seeded fixture is 3857, and the two agreed in every test
until somebody opened a 4326 layer.

**The map is a drop target, which is the second half of the same instruction:** *"map'e
databaseden taşıdığım toc'a gelsin. toc'a taşıdığım map'e katman olarak gelsin."* The tree is
what will be published and the map is what it looks like, so a table dropped on either belongs
to both. Reordering is not offered on the map — a picture has no answer to *where in the drawing
order*, and a drop that meant something the operator did not ask for is worse than one that is
refused.

### 5k. The symbol is chosen while composing

**The owner asked for it with the screen** — *"Katmanın açılır ekranının altında sembolü
gözükecek. Tıklayınca modal bir ekran açılacak ve o katmanın sembolunu değiştirebileceğim."* The
swatch under each layer is the symbol it will be drawn with; clicking it opens a small dialog of
two colours and a width, which is what a fill, a line and a point all need.

**The document travels with the composition**, so the preview redraws with it and the published
layer is stored with it — `PublishRequest.Symbology` → `LayerPublication.Symbology` →
`layer.symbology`, the same column the layer's own symbology screen writes. A dialog that only
changed the swatch would be a picture of a preference.

**Null is not the same as a document that matches the default.** An unset symbology is what makes
the server generate an appearance from the geometry, and that generated appearance is allowed to
improve; a stored copy of today's default would freeze it. `symbology_updated_at` is stamped only
where a document was chosen, because *chose the generated one* and *was never asked* are
different states.

**Everything else about how a layer looks stays on the layer's own screen.** Classes, breaks and
labels are a published layer's business; this is the choice made while composing.

### 5l. The datastore is not one of the databases to compose from

**By owner instruction, given twice** — *"datastore burada olmayacak"*, and then *"datastore
kalksın oradan demiştim hala orada"* when it was still listed. The reason is in the same
conversation as the screen itself: *"Datastore tarafına atılan her tablo otomatik olarak servis
oluyor zaten."*

This screen exists for the databases whose tables are **not** already services. Listing the one
store whose tables are is offering an act with no subject — and worse, it invites somebody to
compose a second service over data that is already served, which is now possible (§5i) and
almost never what they meant.

**What it costs is stated rather than discovered:** a table imported into the datastore cannot
be put into a multi-layer service from this screen. It is served on its own, automatically, and
combining it with others has no route. That is a consequence of the instruction rather than an
argument against it, and it is written here so the next reader does not treat the omission as an
oversight.



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
   ***(Discharged 2026-09-06 — the second branch, and it was settled by never drawing them.)***
   The dialog offers `FeatureServer` and `VectorTileServer`, which are `serves_features` and
   `serves_tiles`, and the ceiling, which is `capability_ceiling` — three things the service row
   already stores and every read face already honours. MapServer and OGC are named on the screen
   as following the feature face rather than left out silently. §5g has been corrected to match:
   it claimed four switches, and two of them had nowhere to be stored.

   **Two things were measured rather than assumed while discharging it.** `Extract` is in
   `ServiceCapabilityLimits.Known` and is never granted by `PrivilegedCapabilities`, so it is not
   drawn. And `Editing` — which §5g said the screen would show as a derived string — is never
   emitted by this server at all; whether an Esri client needs it is [Q-145](../open-questions.md)
   and not this condition.
3. **A composition of a thousand layers is published, and the transaction is timed.** 5h
   writes the service, its groups and its layers in one transaction, and every composition
   anybody has published so far has held three things. A service assembled from a whole
   database is the case where one long transaction stops being free, and nobody has looked.
4. **`POST /admin/featureservices` requires layers, and the drawer's *Create empty service*
   comes off the screen.** 5h decides this and cannot be applied yet: that endpoint is the only
   way to make a service until the Publish path exists. The condition is here so the sequence
   is not forgotten, because the intermediate state — a rule the screen keeps and the API does
   not — is the shape ADR-033 warned about, where the next writer bypasses it.
   ***(Discharged 2026-09-06.)*** Both halves, in that order. The drawer keeps only its group
   form and is titled *Group layers*; Server's page action goes to the Publish screen; the
   endpoint answers **400** naming `POST /admin/publish`, rather than disappearing, because a
   caller with the old address written down is exactly the reader who has to be told where the
   act went. `IAdminCatalog.CreateServiceAsync` went with it — a catalogue method that makes an
   empty service and has no caller is the same rule with a longer fuse.

   **Three conformance classes made empty containers because they were about something else**,
   and moving them cost less than the estimate: every class that touches the whole catalogue is
   in the `catalogue walk` collection, which xUnit runs one class at a time, so CI's two free
   tables are enough for all of them serially. `ArcGisClient.PublishOneAsync` and
   `UnpublishAsync` are the shared shape, written once rather than three times.

   **And the move found three holes in `POST /admin/publish` itself.** It reached the catalogue
   without the folder-name check, without the privilege that shares to the public or the
   organization, and without ADR-028 condition 5's system-service address check — all three of
   which `POST /admin/layers` has, twenty lines apart. That is [D-46](../architecture-debt.md)
   exactly: a second way in that does not carry what the first way carries. Fixed with the
   condition, because the endpoint this one replaces had the third of them and losing it
   silently would have reopened [D-187](../architecture-debt.md).

   **What the retired endpoint had that nothing missed:** it never checked the folder name, so
   `folder: "Utilities"` with any name at all created a published service inside a reserved
   folder. Closing that was not the point of this condition and is the clearest evidence that
   two ways in drift.
6. **The preview is timed against a composition of a database, and the ceiling is chosen with
   a number rather than a guess.** §5j draws every layer on every change the screen cannot
   coalesce, and each layer is a spatial query against somebody else's database. The record
   ceiling is 4,000 per layer and that figure was picked for feel, not measured: nobody knows
   where a preview stops being instant, whether the answer is the row count or the vertex count,
   or what forty layers cost at once. **Until it is measured the screen is fast on a fixture and
   unproven on an estate**, which is exactly the shape §60 warns about from the other direction.

7. **A preview that samples says so on the screen.** Where the ceiling bites, the drawing is
   part of the layer and looks like all of it — and an operator deciding what to publish from a
   picture that silently omits half the features is being misled by the thing built to inform
   them. The server knows when it truncated; nothing carries that to the page yet.

5. **A service with a group is opened by a real ArcGIS client.** `type: "Group Layer"` and
   `subLayerIds` are what the specification says; what Pro and the JavaScript API actually do
   with a group layer served by something that is not ArcGIS Server is not known here, and
   the first person to find out should not be the owner.
