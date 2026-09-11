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

**It starts as EPSG:3857 — owner decision, 2026-09-07** ([Q-146](../open-questions.md)):
*"Q-146 3857 default olsun."* **Nothing in the code changed and that is the point of having
asked.** The box was set to 3857 the first time the screen wired up, and this section was silent
on the default, so the behaviour was an implementation habit that a UX review could not tell
apart from a decision. It is now a decision: a service composed without anybody opening the map's
properties is served in Web Mercator, which is what a web client wants and what the preview
ground is already in.

**What that costs is accepted rather than denied.** An operator whose tables sit in a national
grid, who drags them in and publishes, gets them all reprojected. It is defensible because it is
*visible*: the map's row carries `EPSG:3857` from the moment the screen loads, and every layer
stored in something else carries a ⇄ mark against it (§5n). A default nobody can see would be a
different decision.

**And there is no *each layer's own* — owner decision, 2026-09-09.** Asked whether the Publish
screen owed a one-press way back to that state ([Q-147](../open-questions.md)), the owner
answered that the state should not exist: *"her katman kendi referansında olamaz. hepsi map'in
referansını kullanacak. yani setlenmiş referansı."* **So the question dissolves rather than being
answered** — a control that returns the screen to a state the product does not have is not owed,
and the register row closes on the premise rather than on the design.

**Three things in the code still model the state that was just removed**, and they are the work
this decision creates rather than a detail of it: `PUB_REFERENCES` carries *Each layer's own* as
`code: 0`, `pubSridSaid` starts at *each layer in its own*, and the properties dialog's hint says
that leaving the box empty serves each layer in its own reference. `PublishedLayer.ServedSrid`
carries the same meaning in its own remarks — *null is the meaning; it is this layer's own*.
**None of that is wrong yet**: null is what every service published before migration 39 still
holds, so the fallback stays until those services are migrated. What changes is that it stops
being an *offer*.

**And every face serves in it — owner decision, the same day.** *"wms ve wfs map'in
projeksiyonunda yayınlanacak."* This closes the half of [D-229](../architecture-debt.md) that was
left open: the feature face reads `service.srid` and agrees with itself, and the map faces did
not read it at all — WFS wrote each feature type's `DefaultCRS` from the **layer's** storage
SRID, and WMS advertised a fixed `EPSG:4326 · EPSG:3857 · CRS:84` regardless of what the service
names. Both now take `ServedSrid`, which already travels beside the layer for
[D-179](../architecture-debt.md)'s reason and needed no new plumbing.

**Built 2026-09-09, and the before is worth keeping because it was invisible.** With
`ci_buildings` set to EPSG:5253 through `PUT /admin/services/{name}/srid` and nothing else
changed, both capabilities documents came back **byte-identical** to the ones the same server
wrote with the service unset. After: `wfs:DefaultCRS` is `urn:ogc:def:crs:EPSG::5253` for that
type, the WMS layer states `EPSG:5253`, and a `GetFeature` with no `srsName` answers
`4443679.36 1000592.15` — northing first, 5253's own order, rather than metres relabelled. The
rule is one expression, `PublishedLayer.PublishedSrid`, read by both faces rather than computed
twice; a service whose reference is a **written definition** (§5m) falls back to the table's code
on these two faces, because neither `wfs:DefaultCRS` nor a WMS `CRS` element has anywhere to put
one.

**WMS keeps exactly one reference nobody chose, and it is a judgement rather than a reading.**
The root layer states `CRS:84` — `EPSG:4326` on 1.1.1, which has no `CRS:84` — and every named
layer inherits it. Against keeping it: *published in the map's projection* is one claim, and any
second entry is a second thing a client can pick. For keeping it: 1.3.0 §7.3.3.1 makes `CRS` a
**required** `GetMap` parameter, so on this face there is no default at all and the advertised set
*is* the publication — a document whose only reference is a national grid turns away every client
that cannot look one up. This server also writes every layer's `EX_GeographicBoundingBox` in
WGS 84 and the world box in `CRS:84`, so declining to list it would leave the document stating
boxes in a reference it claims not to support. §7.2.4.6.7 requires *at least* one per layer and
forbids no extra. One inherited entry is not the list the owner declined; three fixed ones were.

**A layer stored somewhere else also states its stored code, and that is not a second offer.** The
layer's own `BoundingBox` is written in the table's metres — a decision
`EmptyLayerStillHasABoundingBoxTests` holds, on the grounds that replacing an extent with the
world makes every document conformant and every extent useless — so the code has to be listed or
the document states numbers in a reference it says a client may not ask for. It appears because
the document already uses it; a service that has chosen nothing states exactly one.

**One thing on the request path moved with the document, and it is the one that could have lost
data quietly.** WFS 2.0 makes an un-annotated `bbox` mean the feature type's `DefaultCRS`, which
`WfsBoundingBox`'s own remarks have always claimed it did. Left reading the table, a service
published in a national grid would advertise the grid, take the client's grid numbers as Web
Mercator metres and match nothing — a 200 with an empty collection. Measured after: the same
un-annotated box matches **1** feature in 5253's numbers and **0** in 3857's, and reverses when
the fifth field names 3857.

**The tile face stays exempt and that is not an oversight** — a vector tile scheme is defined in
Web Mercator, `VectorTileServerMetadataWriter` refuses any other extent, and a document whose
`fullExtent` and `tileInfo` disagreed would make a client fetch the metadata and then no tiles
([D-49](../architecture-debt.md)).

**And MapServer serves in it too — owner decision, 2026-09-11.** *"MapServer da uysun."* It was
the last face reading the table: internally consistent, since it stated and drew 3857 for a 3857
table, but a service set to 4326 told a MapServer client *3857* while every other face said 4326.
Its service and layer documents now state `ServedSrid` with the extent moved into it, by the
`ServedExtent` the FeatureServer document already uses. **The export moved with the documents,
because the document and the drawing are one claim**: an Export Map `bbox` with no `bboxSR` is,
by ArcGIS's published reference, *in the spatial reference of the map*, and this server had read
it as 4326 regardless — wrong before this decision for every service not stored in 4326. It now
reads the first drawable layer's `PublishedSrid`, the same reference the document states
([D-229](../architecture-debt.md), closed).

**What this does not do is close the capability.** A caller may still name another reference per
request — `outSR` on ArcGIS, `srsName` on WFS, `crs` on WMS — and it is still honoured, which the
owner asked about directly and which is measured: with no `srsName` a feature comes back in the
service's reference, and `srsName=EPSG::5253` comes back in 5253.

**That sentence was written before the code and was false for a few hours, which is worth leaving
visible rather than tidying.** Measured on the running fixture before the change, with
`ci_buildings` served in 5253: no `srsName` came back in **3857**, the table's. It is true as
written from 2026-09-09; until then the *capable of* half held and the *published in* half did
not, which is the opposite of what a reader would have taken from it. On WMS there is no default
to be wrong about — `crs` is a required parameter — and `crs=EPSG:32636`, advertised nowhere,
still draws: 582 inked pixels of 16,384 beside the advertised EPSG:3857's 722. That gap is
[ADR-060](ADR-060-the-axis-order-comes-from-the-register.md) condition 4 and this decision does
not close it. **Published in** and **capable
of** are two different claims, and only the first is what a capabilities document is for.

~~**Empty is still a real choice and not a missing answer** — the service then serves every layer
in whatever its own table holds, and `service.srid` is null. Whether that choice is reachable by
anything other than clearing the box is [Q-148](../open-questions.md), open.~~

**Struck 2026-09-10, and it had been contradicting the paragraph thirty lines above it for a
day.** That paragraph records the owner's decision of 2026-09-09 that *each layer in its own* is
not a state a service may be in; this one, written on 2026-09-07, called it a real choice and
pointed at an open question. Both stood in the same section. **It also cited the wrong
question** — the reference question is [Q-147](../open-questions.md); Q-148 is about bounding
the preview by vertices.

**This is the propagation shape [D-130](../architecture-debt.md) records**, at its smallest and
most embarrassing: the decision was rewritten in place and the sentence it replaced was left
below it, in the same section, for the next reader to choose between. Found while re-reading
§5c to write a debt row that turned out to be already written.

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

**Written 2026-09-08, three days after it was decided, and the gap is the finding.** Everything
above was `ACCEPTED` on 2026-09-05 and had no code behind it: `pbName` was read when Publish was
pressed and nowhere else, and the replacement half — the whole of the second paragraph — existed
in no file. A collision came back as a translated constraint violation, which is a good refusal
in the wrong place, and *offered as a replacement* had never been implemented at all. It survived
a green suite for the same reason [§5k](#5k-the-symbol-is-chosen-while-composing)'s missing
function did: no test pressed the name box, and a dialog that refuses late looks exactly like one
that refuses early until somebody composes twenty layers first.

**What was built.** `GET /admin/publish/name?name=&folder=` answers one of five reasons — `free`,
`yours`, `taken`, `system`, `folder` — and the console asks it 250 ms after the last keystroke,
sequenced so a superseded answer is dropped. `POST /admin/publish` takes `replace: true`, and
refuses an occupied address without it.

**Every refusal the publish makes about an address is made by the check, in the same order**, and
that is the property worth stating rather than the endpoint. A check that knew about existing
services and not about reserved folders would answer *free* about a name the publish then
refuses — which is worse than not checking, because the operator has been told it was fine.
Asserted directly: the conformance test asks the check about `Utilities/Geometry` and then asks
the publish, and the pair has to agree. It also taught something the test was written not
expecting: the answer is `folder`, not `system`, because `Utilities` is a reserved folder and is
refused a step earlier. The `system` branch is unreachable through any address a person can type
today, and it stays because the publish's own guard does.

**One query, not a listing** — condition 1's first half, and it was a choice rather than a
measurement. `FindServiceAtAsync` matches on `coalesce(lower(folder), '')` and `lower(name)`,
which is the expression `service_name_in_folder_ci` is built on. A predicate shaped differently
from the constraint it predicts would agree with it for every name typed so far and disagree the
first time two folders differed only in case — in the direction that says *free*.

**The replacement keeps the service's id, and this is a decision taken while implementing rather
than one the owner made.** It is written here so it can be overturned like
[§5i](#5i-one-table-is-one-layer-within-a-service--and-the-schemas-answer-was-not-a-decision)
was. A service **is** its item — [ADR-056](ADR-056-an-item-is-its-own-thing-and-a-service-is-one-kind.md)
stores no item row, so the id somebody shared or bookmarked is the service row's id — and
replacing by delete-then-create would keep the URL and break every one of those, which is the
opposite of what *replace* means to whoever pressed it. So the row survives, every column the
composition carries is rewritten, `created_at` goes on saying when this address was first
published, and `updated_at` moves. Three columns are deliberately not written: the owner, because
only the owner may replace; `created_at`; and the id itself.

**Its layers do not survive it**, and that is not a compromise: they are deleted and reinserted
inside the same transaction, so every layer gets a new id — which is what republishing has always
done ([D-34](../architecture-debt.md)) and what makes the previous composition's cached tiles
unreachable rather than wrong. The endpoint purges them by the ids the *transaction* reports
rather than the ids the check read, because a layer could have been published into that service
in between.

**In one transaction, for the reason §5h gives about creation.** Emptying a service and refilling
it in two calls has a window in which the address exists and serves nothing — the empty residue
this ADR refuses to create on purpose, reached by accident instead.

**Only the owner, and an administrator is not an exception.** `content:publishFeatures` says a
person may publish, not that they may destroy what other people published. An administrator who
means to take the address has a route already — delete, then publish — which is two deliberate
acts and is audited as two.

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

**And where it bites, the screen says so — built 2026-09-08, condition 7.** `DrawLayerAsync`
returns how many features it drew; the endpoint compares that against the ceiling per layer and
answers `X-Graticula-Ceiling` always and `X-Graticula-Sampled` when it bit, and the line over the
map names the layers. Until then the class's own comment read *the drawing is a sample of the
layer and the screen says so* while nothing anywhere reported it — a sampled drawing and a
complete one were the same picture, and an operator judging a composition by eye had no way to
tell which they were looking at.

**A layer at exactly the ceiling is reported too**, which is a false positive of one and is the
safe direction. The sentence says *reached the ceiling, so this may be part of it* rather than
claiming a completeness that would cost a second count query per layer to establish.

**Names travel percent-encoded, and indices were the first idea.** A header value is ASCII and a
layer's name is whatever the operator typed. Indices looked cheaper until the mismatch showed:
the composition's numbering counts groups as well as layers, and the drawing loop walks the
layers alone and bottom-first, so an index emitted there would have meant something different
from the index the screen draws — and would have meant it silently.

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

**This section described something that did not exist for a day, and the record is the point.**
The swatch, the *Symbol…* item on the right-click menu, the dialog's markup, both click handlers
and `pubSymbolDocument` — which turns the answer into the CIM document above — were all written.
`openPubSymbol`, the function both handlers call, was not: `grep -c "function openPubSymbol"`
answered `0` while two controls offered it. Both threw a `ReferenceError` the click handler
swallowed, `node.symbol` was set by nothing, and **every composition published `symbology: null`**
— so the paragraph above was a description of an intention.

**It survived a full green suite** because no test pressed either control, and because a swatch
drawn in the default colours is indistinguishable from a swatch showing a layer nobody has
restyled. It is ADR-034's prohibition — *a control is not drawn for a feature that does not
exist* — reached from the direction that register does not usually catch, since the feature was
believed to exist by everyone including the document you are reading. Written 2026-09-07, with a
test that presses the swatch and follows the colour to the row.

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



### 5m. A reference may be written out, not only numbered

**By owner decision, 2026-09-06:** *"epsg güzel ama wkt de kabul etmemiz lazım."* A national grid
a customer uses, or a local system, may have no EPSG number at all — and then the definition is
the only way to name it. §5c said a service names its reference; this says a name is a code **or**
a definition.

**One or the other, never both.** Migration 41 adds `service.srid_wkt` beside `service.srid` with
a check refusing a row that carries two, and `ServedReference` is the one type that reads the
pair — so no reader has to decide which wins, because no row can pose the question.

**Nothing is written into anybody's `spatial_ref_sys`, and that is the decision.** The cheap
route is to insert the definition under a spare code and keep using integers everywhere. It
writes into the database a *registered* source points at, which belongs to somebody else — the
same line ADR-034 §5k draws when it refuses to drop a registered table with its service.
**Measured before choosing:** PostGIS 3.4.3's `ST_Transform(geom, '<wkt>')` gives the same
coordinates as `ST_Transform(geom, 5254)` **to the last digit** and needs no row, so the cheap
route buys nothing it is allowed to spend.

**A geometry transformed to a definition carries SRID 0** — measured the same way — so the
documents say `wkt` rather than `wkid`, which is what the ArcGIS spatial reference object has
always allowed. Both documents say it: the layer's `extent` and the query's own
`spatialReference`, which is D-229's rule applied to the second kind of reference.

**Refused before anything is written.** `POST /admin/publish` asks the projector whether it can
serve in the reference chosen — `KnowsAsync` for a code, a one-point transform for a definition —
because the alternative is a service in the catalogue that fails on its first query, after the
operator has moved on.

**And it cost an hour to the wrong kind of default.** `ProjectToDefinitionAsync` was added to
`IProjector` with a default implementation answering *this projector cannot*. `BreakingProjector`
decorates the real one and inherited that default, so the server refused every definition PostGIS
had just accepted by hand — with a message blaming PROJ. **A default answer makes *cannot* and
*nobody wrote this* the same word.** The default is gone and the compiler asks every implementer,
which is four of them and worth it.

### 5n. The reference is a property of the map, and is set from the map

**By owner instruction, 2026-09-07:** *"onu şu anda bulunduğu yerden alıp map'e sağ tıklayınca
açılan bir ekrana koyalım. sonuçta map'in projeksiyonu hepsini kapsayacak."* §5c settled that a
service names one reference and every layer is served in it; this settles where somebody says so.

**It had been a box on the page's toolbar**, between *Preview* and *Clear*. That put a property
of the map among the verbs — three buttons that do something and one field that is something —
and it read as a fourth act to perform each session rather than as a fact about the map. The
owner's sentence is the argument: *the map's projection covers all of them*, so it belongs to the
map.

**Right-clicking the drawing and right-clicking the map's row open the same menu**, because they
are two views of one thing; *Map properties…* is on it, and the reference is chosen there. The
tree's root row carries the answer beside the map's name, so it is readable with the dialog shut
— a property visible only inside a dialog is one that is forgotten between composing and
publishing. ArcGIS Pro's contents pane is the reference the owner named for this screen and keeps
the same property in the same place.

**Moving it exposed a second control answering the same question, wrongly.** The Publish dialog's
*Served in* line worked the reference out for itself from the box and only knew how to read a
code — so a definition pasted under 5m made **the last line an operator reads before pressing
Publish** say *each layer's own* while the request it then sent carried the definition. The
composer's own line was right about the same service at the same moment. That is
[D-46](../architecture-debt.md): one behaviour in two places, one copy taught about definitions
on 2026-09-06 and the other not. The repair is that the sentence is written once, where the
question is answered, and the map's row, the map's properties and the Publish dialog all read it.

**The review of the move found six things the move did not cause and one it did.** The one it
caused is above. The six were already there and were only reachable once somebody read the new
route end to end: the map's row carried its reference in a `.pubsr` badge styled as
`.pubrow .pubsr`, which `.pubroot` does not match — so the badge that exists to be read at a
glance had no rule at all and rendered at the row's own weight, beside the word it was meant to
sit under; the Publish dialog's refusal state had no colour, because `.pbsridsays.bad` exists and
`.pbreadonly.bad` did not; `Escape` dismissed either dialog without the redraw both close buttons
did, so a colour changed and dismissed left a stale picture — the redraw now hangs off `close`,
which fires for every way out, rather than off the two buttons that were the only ways anybody
had tried; the symbol dialog's status line was not a live region while its twin was; the empty
tree explained grouping and not where the coordinate system lives; and `#pubmenu` had carried
`role="menu"` with `role="menuitem"` items since it was written **while implementing none of what
those roles promise** — focus never entered it, the arrows did nothing, and `Escape` left focus at
the end of the document. That last one matters here more than it did yesterday: the reference is
now reached only through that menu.

**A claimed role is the same defect as a called function that was never written**, one paragraph
up. Both tell a reader — a person, a screen reader, a maintainer — that something exists because
the name for it is present.

**Two of the findings are the owner's to answer and are not taken here**, as
[Q-146](../open-questions.md) and [Q-148](../open-questions.md): whether a new composition should
start in EPSG:3857, as it does, or start empty and serve each layer in its own until somebody
chooses; and whether the properties dialog needs a one-press way back to that unset state, which
the symbol dialog has an equivalent of and this does not. The first is a real behaviour: today an
operator who never opens the properties publishes every layer reprojected to Web Mercator without
having been asked. §5c settles that a service names one reference and is silent on what it starts
as, so the current answer is an implementation default wearing a decision's clothes.

### 5o. A reference is named, and every one this server knows can be found

**By owner instruction, 2026-09-07:** *"tanımlı tüm srid leri gösterebilir miyiz. mesela 3857
yazınca web mercator yazıyor ama 4236 yazınca adı çıkmıyor."* §5n moved the box; this is about
what the box can tell you. It knew five references by name, from a constant in the page, and for
everything else it asked the server *can you project to this* and reported yes. **Yes is not an
answer an operator can check.** It says the code is usable; it does not say which reference it
is, and those are different questions when 4236 is Hu Tzu Shan 1950, its area of use is Taiwan,
and it is one keystroke from 4326.

**The name comes from the projection database, because a list written here would drift.** That is
the same argument that put *can you project to this* on the server in the first place — a
console-side table of references is a second opinion that goes stale the first time somebody
upgrades PROJ. `IProjector.ReferencesAsync` is the port; `GET /admin/references/{srid}` gains a
`name`, and `GET /admin/references?q=` answers the twenty that match a word or a code.

**Read from `spatial_ref_sys` rather than `postgis_srs_all()`, and that is measured.** Both know
the names. The function materialises the whole PROJ database per call and answered one search in
**1.37 s** against PostGIS 3.4.3; the table answered the same search in 0.61 s and an exact code
in **0.42 ms**. The whole list — 6,184 EPSG rows, 8,486 with other authorities, 174 KB of names —
is read once per process at **874 ms** and searched in memory afterwards, so a keystroke costs a
string comparison rather than a query. The table is also the right authority for a second reason:
it is what `KnowsAsync` already asks, so a reference this can name is one this server said yes to.

**Nothing is written into anybody's `spatial_ref_sys`**, which §5m already decided and which rules
out the obvious fix for the search being slow — an index on a registered database's own catalogue
table. Reading it once and keeping the answer is what that decision leaves.

**Ranked with the exact code first, then EPSG before other authorities.** The first ranking sorted
by name length and put ESRI's `World_Mercator` above `WGS 84 / Pseudo-Mercator` for a search of
*mercator*; a list whose first answer is not the one everybody means is a list nobody reads twice.
The screen also says how many matched and how many were not sent, because a box offering twenty of
eight thousand looks exactly like a box offering everything there is.

**A definition is not a search.** The one box takes a code or a pasted WKT (§5m), and the
suggestion is guarded by length rather than by asking the operator which kind of thing they are
typing — the same rule that tells the two apart everywhere else on this screen.

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

## 6a. The screen went through the ux-designer, and what it found

**Run 2026-09-08** — the owner's standing instruction — and recorded in
[design-publish-and-fields-2026-09-08.md](../reviews/design-publish-and-fields-2026-09-08.md)
rather than here, because a review has findings and dispositions and an ADR has a
decision.

**The headline is that the screen was unusable without a mouse.** Not awkward:
unusable. The Databases pane had no tab stop anywhere, so a keyboard operator
could not open a database, could not open a schema, and could not get one table
into a composition — which is the whole of what this decision built. Selecting two
layers to group them was mouse-only for the same reason, so §5b's groups were
unreachable for them entirely. Both are fixed and both are now asserted as *the
task* rather than as attributes: a test that checked for `tabindex` would pass on a
screen where pressing Enter did nothing.

**And the §5e name check, written that morning, had no live region** — so the
sentence that decides whether somebody overwrites a published service arrived
silently, 250 ms after they stopped typing. Two paragraphs of that exact shape in
the same file already had one.

## 7. Conditions

1. **The name check is measured against a folder with a thousand services in it.** 5e asks
   for a request while somebody types; whether that is one query or a listing walked in the
   browser decides whether the screen is usable on a full server, and nobody has looked.

   ***(Discharged 2026-09-08 — measured, and it was written `PARTLY DISCHARGED` for the hour
   between building the check and running the benchmark.)*** *Which implementation it is* is
   settled: `FindServiceAtAsync` is a single lookup on the expression
   `service_name_in_folder_ci` is built on, so a keystroke costs one indexed read and never a
   catalogue walk. *Whether it is fast enough* is now measured
   ([benchmarks/publish-scale](../../benchmarks/publish-scale/RESULTS.md)): forty checks against
   a folder of **3** services and forty against the same folder holding **1,000**, and the size
   does not move it — **5.7 ms** median on the full folder against 15.1 ms on the empty one,
   which is the empty run paying for a cold pool rather than the index doing something clever.

   **The p95 is ~17–18 ms in all four sets while three of four medians are under 8**, which is
   bimodal and is not the query: an index lookup does not have two speeds. It is the same in an
   empty folder as a full one, so it belongs to the harness — TLS or a GC pause — and is left
   un-chased because 18 ms is inside a keystroke either way. Written down rather than smoothed
   out, because a benchmark that reports only the number it went looking for is one nobody can
   re-read later.

   **And it moved the reason for the 250 ms debounce without moving the number.** At 6 ms a
   check the debounce is not protecting the server from cost; it is protecting the operator from
   a sentence that changes under their fingers. That is still a reason, and it is a different one
   from the one it was written with.
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
   ***(Discharged 2026-09-08 — measured.)*** A single `POST /admin/publish` naming **1,000
   layers** over a thousand tables answered **201 in 0.68 s**
   ([benchmarks/publish-scale](../../benchmarks/publish-scale/RESULTS.md)). The tables were
   empty on purpose: this is the catalogue transaction, which is what the condition asks about.

   **The transaction is the cheap part of publishing, and the round trip is not.** Inside it a
   row costs ≈0.7 ms; the same thousand layers published one service at a time cost 25 ms each.
   *One long transaction stops being free* turns out to be the wrong worry by a factor of
   thirty-five — the worry that survives is anything that publishes one at a time.

   **One number nobody was looking for.** Deleting those services cost 58 ms each against 25 ms
   to create, because the delete path walks group indices and unpublishes layer by layer where
   publishing writes them in one statement. Recorded so the next person timing a bulk teardown
   does not think they have found a fault.
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


   ***(Discharged 2026-09-08 — measured, and the measurement changed what the number is a
   bound on.)*** A generated corpus crossing row count with vertex count, drawn through the real
   endpoint at a fixed frame ([benchmarks/publish-scale](../../benchmarks/publish-scale/RESULTS.md)).

   **The condition asked whether the answer is rows or vertices. It is vertices.** Holding rows
   fixed and going from 5 vertices to 500 multiplies the time by **ten**, at every row count.
   Holding vertices fixed and going from 250 rows to 4,000 multiplies it by two at 5 vertices.
   **4,000 simple polygons draw in 61 ms; 1,000 complex ones take 291 ms.** The ceiling is on
   the cheaper variable.

   **And it bounds what is returned, not what is read.** A 16,000-row table capped to 4,000
   drawn still costs **1,113 ms** at 500 vertices, against 716 ms for a 4,000-row table drawn
   whole — the `LIMIT` does not stop PostGIS reading and simplifying the candidates. The
   ten-fold difference is spent in the database: the response is 21–22 KB either way, because
   simplification to one pixel happens there.

   **A composition of a database is linear, at about 7 ms a layer.** One layer 40 ms, fifty
   layers 397 ms, on simple data — the right order of magnitude for a screen that redraws on pan.

   **The number stays at 4,000, and now it has a criterion.** The ceiling exists to stop a
   *large* layer making the preview unusable, and on that shape it is measured to work: 8,000
   and 16,000 rows cost what 4,000 costs. Lowering it would buy nothing on the case that is
   actually slow — 1,000 dense rows already take 291 ms — and would make the drawing a sample
   far more often, trading a picture the operator can trust for a problem it does not fix.
   **That choice is mine rather than the owner's**, and it is written out so it can be
   overturned; what is no longer available is defending 4,000 by feel.

   **What is left is not this condition.** A single dense layer costs a second and fifty would
   cost a minute, and no per-layer row ceiling reaches that. Whether a preview should bound
   vertices rather than rows — and how, since a `LIMIT` cannot express it — is
   [Q-148](../open-questions.md).
7. **A preview that samples says so on the screen.** Where the ceiling bites, the drawing is
   part of the layer and looks like all of it — and an operator deciding what to publish from a
   picture that silently omits half the features is being misled by the thing built to inform
   them. The server knows when it truncated; nothing carries that to the page yet.
   ***(Discharged 2026-09-08.)*** The last sentence was the whole problem and it was half wrong:
   the server did **not** know. `DrawLayerAsync` returned `Task`, the ceiling went into the query
   and nothing outside it could see whether it had bitten — so *carrying it to the page* had
   nothing to carry. It now returns what it drew, `POST /admin/publish/preview` answers
   `X-Graticula-Ceiling` always and `X-Graticula-Sampled` when it bit, and the line over the map
   names the layers and says the published service is not limited by it. A layer at exactly the
   ceiling is included, which is a false positive of one in the safe direction. §5j carries what
   the header encoding cost and why indices were rejected.

   **The number in the ceiling is condition 6 and is untouched by this.** Saying *this is a
   sample* is honest whatever the bound is; whether 4,000 is the right bound is still a guess
   nobody has measured, and discharging this one must not be read as having answered that.

5. **A service with a group is opened by a real ArcGIS client.** `type: "Group Layer"` and
   `subLayerIds` are what the specification says; what Pro and the JavaScript API actually do
   with a group layer served by something that is not ArcGIS Server is not known here, and
   the first person to find out should not be the owner.

   **PARTLY DISCHARGED 2026-09-08. The JavaScript API half is run and found two defects; Pro is
   not run here and needs a machine with Pro on it.** This is exactly what the condition was for:
   the document was correct in every particular the specification names, and the client never got
   past its second request.

   **Finding one, and it was fatal.** `Layer.fromArcGISServerUrl` reads
   `/FeatureServer?f=json` and then immediately `/FeatureServer/layers?f=json` — *All Layers and
   Tables*, one document holding the full definition of every layer so a client need not make a
   request per layer. **This server answered 404** and the API abandoned the whole service:
   `request:server, Unable to load … status: 404`. Written, and built from the same function
   `/FeatureServer/{id}` answers with rather than a second writer, because the served extent, the
   symbology and the relationship list have all been added to that document since it was written
   and a copy would have missed each one silently — [D-46](../architecture-debt.md).

   **Finding two, which the first uncovered.** The layer resource did not carry `parentLayerId`
   **at all**. The service document said layer 2 was inside group 1; the layer's own document had
   no key for it, so a client that read one layer could not tell it was in a group. Both documents
   now say it, and the conformance test asserts they agree — two documents disagreeing about
   which group a layer is in is the shape this condition is about, because a client reads one of
   them.

   **What the API does with the group is not a defect of ours, and that was measured rather than
   assumed.** It flattens: one group layer titled after the service, the feature layers as flat
   children, also titled after the service. The same call against
   `sampleserver6.arcgisonline.com` — ArcGIS Server's own — produces the **identical** shape. The
   constructor ignores group layers for everybody. Recording that difference as our bug was the
   available mistake, and one request to somebody else's server is what avoided it.

   **What is left is Pro**, which is the client the owner's users actually have and which cannot
   be run from here. The condition stays open for it.
