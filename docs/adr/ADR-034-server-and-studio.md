# ADR-034 — Two surfaces over one API: Server for the operator, Studio for the publisher

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the split · `MEDIUM` for where each screen lands |
| **Decided** | 2026-08-17 |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-020](ADR-020-admin-console-and-service-status.md) — one console becomes two surfaces over the same API |

---

## 1. Context — one console shows everybody everything, and then refuses

The owner's proposal, 2026-08-17: *"bizim console dediğimiz uygulama server olsun. arcgis
server'a benzesin. portala benzer de studio olsun. ama sadece admin kullanıcıları 2 ortama
birden girebilsin. diğer kullanıcılar sadece studio ya girsin."* It answers a question they
asked on 2026-08-16 and which was left open — *"studio yapmak mantıklı mı"*.

**The defect it fixes is in the console today.** Every surface renders for anybody who signs
in: the tab strip is built once, and each section loads independently and reports its own
failure ([ADR-020 §5e](ADR-020-admin-console-and-service-status.md)). That isolation was
built for a half-broken *server*; against a half-privileged *reader* it produces a console
whose screens fail one at a time. A publisher signing in today sees Operations, Route
governance and the Anonymous view, and learns what they may not do by being refused four
times.

**And the privileges already draw the line the UI does not.** Read out of the endpoints
rather than assumed:

| Screen | The privilege its endpoint requires |
|---|---|
| Layer listing | `admin:viewAllContent` |
| Capabilities, limits, folders, service listing | `admin:manageServer` |
| Health, route governance, refresh | `admin:manageServer` |
| Publish and create | `content:publishFeatures` |
| Symbology, stored style | `content:publishFeatures` |
| **Cache lifetime** | `content:publishTiles` |
| **Data sources** | `content:registerDataStore` |
| Sharing | `sharing:shareToOrganization`, `sharing:shareToPublic` |

Two of those were a surprise to me and both are right: **cache lifetime** is the decision of
whoever knows how often the data changes ([A-028](../architecture-assumptions.md)), and
**registering a data source** is a publisher's act. My instinct had put both under
administration; the privilege model had already put them under content, and it was correct.

## 2. Alternatives considered

### Alternative A — one console that hides what you cannot use

**Argument for.** No second surface, no second URL space, no duplication. The privileges are
already known at sign-in, so the tab strip could simply omit what this reader cannot reach.

**Argument against.** It solves the refusals and not the confusion. A publisher's tools and
an operator's tools are different *jobs*, not different permissions on one job: the operator
watches a server, the publisher shapes content. One screen sequence for both means neither is
designed for anybody — which is the state ADR-020 §5b already criticised in a different
product.

### Alternative B — two surfaces over one API, one deployable (chosen)

**Argument for.** It matches the jobs, it matches ArcGIS's own shape (§4), and the gate is a
privilege the API already enforces rather than a rule invented in a browser. One server, one
API, one stylesheet, one map module, one router with two roots.

**Argument against.** Two surfaces are two places for the same fact to live, which is
[D-46](../architecture-debt.md)'s recurring debt — six recorded instances before today.

### Alternative C — two deployables, a server and a portal

**Rejected by [ADR-019](ADR-019-portal-server-split.md)**, which fused Portal, Server and
Data Store into one deployable on purpose. Nothing here is new evidence against that: the
split being asked for is between *audiences*, and audiences do not need their own process.

### Alternative D — adopt the reference's console

**Rejected, and Q-93 is the reason.** Building our admin surface to another vendor's console
makes our API a client of their UI: every screen they change becomes a contract we did not
choose. Their route map was read and its *rules* taken (frozen URLs, missing-binding states,
one canonical surface per exception) — [reading log](../research/reference-reading-log.md) —
which is the useful half.

## 3. Counterarguments to the preferred option

**Studio is second and may rot.** [Q-06a](../open-questions.md) makes the GIS administrator
the primary user and v1-scope is written around them. A surface built for the audience the
product is not primarily for is the surface that gets half-finished — and a half-finished
Studio is worse than no Studio, because it implies a self-service story we have not built.
The honest mitigation is scope: Studio holds what already exists and is content-privileged,
and gains nothing speculative.

**The split multiplies the D-46 risk at exactly the wrong moment.** Symbology authoring
(ADR-033) is being built now and lands in Studio; the layer settings pages are in Server. A
layer's appearance and a layer's limits are then two screens in two surfaces, and *both* are
about one layer. Condition 2 exists for this.

**One reader, two mental models.** An administrator is also a publisher — they have both
privilege sets and will move between the surfaces constantly. If moving is expensive, they
will resent the split that was built for somebody else. So the two surfaces share a header
and one keystroke moves between them; they are not two applications.

## 4. Evidence

**ArcGIS's own split, from Esri's documentation rather than from memory.**
[Administer a federated server](https://enterprise.arcgis.com/en/portal/latest/administer/windows/administer-a-federated-server.htm)
states that Server Manager may be reached only by a portal account in the **administrator or
publisher** role — not viewer, not user, not a custom role, and *not the primary site
administrator*. And that once federated, *"ArcGIS Server users and roles are replaced by those
of the portal"*. So the two-environment shape with an identity boundary is the real product's
shape, and the owner's rule is a stricter version of it.

**Ours is stricter for a reason rather than by accident.** ArcGIS lets a publisher into Server
Manager because that is where they would look at their own service's status and cache. In our
surfaces, everything a publisher needs is content-privileged and therefore in Studio —
including cache lifetime, which is the exact case Esri's split would send them to Server for.
So `admin:manageServer` is the whole gate, and no publisher is missing a tool because of it.

**The services directory hands off to the viewer, and we already do it the stricter way.**
[Connecting the services directory to your portal](https://doc.esri.com/en/arcgis-enterprise/latest/administer/connecting-the-arcgis-server-services-directory-to-your-portal.html)
describes configuring the directory's preview to open in *your own* Map Viewer instead of
ArcGIS Online's, along with the JavaScript API, SDK and CSS URLs, and warns the SDK *"must be
from the 4.x release"*. Two things follow:

- **The directory is a Server surface that links into a Studio one.** That is the relationship
  our split needs, described by the product we are compatible with: browse a service
  administratively, open it in the viewer to look at it. §5d.
- **Their default leaks and ours does not.** By default their preview hands the service URL to
  arcgis.com, which needs an ArcGIS account — the account this product exists so nobody needs.
  Our directory has always linked to our own viewer, and the code says why. What they make
  configurable, we made the default; what they warn about (a 4.x SDK), we pin.
- **But the SDK location being configurable is a real gap of ours.** Esri exposes it so a
  deployment can serve the SDK locally. We hard-code `https://js.arcgis.com/4.29/` in three
  places and in the Content-Security-Policy, so an air-gapped deployment
  ([Q-15](../open-questions.md)) loses the map with no setting to change. §5e.

## 5. Decision

### 5a. Two surfaces, one server — the surface is the path

**`/server/` and `/studio/`**, one application over one API, one stylesheet, one map module.
Not two deployables (ADR-019), not two identity stores
([ADR-015 §5a](ADR-015-authentication.md)), not two viewers.

**The surface is the path and the screen is the hash** — `/server/#/services/turkiye`,
`/studio/#/content`. The first attempt put the surface in the hash and left the application at
`/console`, which the owner caught immediately: *"console neden hala ayakta. console yerine
server kullanacaktık ya."* They were right, and not only about the word: an environment is what
a path is for, and a name nobody uses any more should not be in the address bar. It is also
how the product this follows separates them — Server Manager and the portal are two
applications at two paths.

**One directory served twice, not two copies.** Condition 2 asks for one stylesheet and one map
module across both surfaces; two mounts over one folder is the cheapest way to mean it, because
there is nothing to keep in step. `/console/*` answers 301 to `/server/*`, which honours
ADR-020 §5c's frozen-URL rule for the one case that rule is for — and a reader without
`admin:manageServer` is then moved on to Studio by the application, since the redirect cannot
know their privileges.

**A screen asked for in the wrong surface is a navigation, not a refusal.** `#/sources` in
Server is Studio's data sources: the reader named a screen, not an environment.

The header is shared and names which surface you are in, with one control that moves between
them — visible only to a reader who may be in both.

### 5b. The gate is a privilege, not a role

**Server requires `admin:manageServer`. Studio requires only being signed in.** This is not a
new rule: it is the privilege those endpoints already demand, moved to where the reader meets
it. A reader without it sees no Server tab, and `#/server/anything` answers with a sentence and
sends them to Studio — a 403 toast is a refusal, not an answer.

**Roles are deliberately not consulted.** ADR-018 makes privileges the unit and roles a bundle
of them; gating a surface on a role name would put a second authorization model in the
browser, where it can disagree with the first.

### 5c. Which screen is where, derived from §1's table

| Server | Studio |
|---|---|
| Services, with capability ceilings and limits (ADR-031) | My content: what I own and what is shared with me |
| Folders, and creating them | Publish and create — designed, imported, registered |
| Start and stop | Symbology (ADR-033), including the stored style |
| Operations: store, runtime, caches, route governance | Cache lifetime — `content:publishTiles`, A-028 |
| The administrative all-content listing | Sharing |
| Data sources, and registering one | The anonymous view |
| Audit | The map viewer and the query page |

A layer therefore appears in both, and that is correct: its *limits* are the server's business
and its *appearance* is the publisher's. Each surface links to the other's page for the same
layer, so the split never means a dead end.

**Two rows moved on 2026-08-17, both by the owner, and one of them was already right on paper.**

**Data sources moved to Server**, and this table had it in Studio: *"data sources studio'nun değil
server'in bir seçeneği. onu da sadece admin ayarlayabilir."* — data sources is Server's option, not
Studio's, and only an administrator configures it. It had been placed beside publishing on the
reasoning that you register a source in order to publish from it, which is true and is not the
question. **Registering is not publishing.** Publishing puts a table on the map; registering hands
this server a **credential for somebody else's database** and adds a machine the whole deployment
then depends on. Its failure modes are operational — a connection down, a schema changed, a
password rotated — and the person who answers for those is the administrator. It is also the only
act on either surface whose blast radius is outside our own store.

So `content:registerDataStore` **moved from the publisher role to the administrator role**, which
is how the surface and the API are kept in agreement: had only the tab moved, a publisher would
have held a privilege for a screen they cannot reach, which is the *"privilege with nothing behind
it"* complaint [D-56](../architecture-debt.md) makes about `admin:manageMembers`. Changing the grant
rather than the endpoint means the privilege keeps its name and its meaning. **This is narrower than
ArcGIS Portal, where registering a data store is a publisher privilege**, and narrow is the safe
direction — the same shape as [D-20](../architecture-debt.md)'s note about `features:edit`.
`RolesTests` asserted the old placement and now asserts the new one, keeping the part the decision
did not change: a plain user has neither.

**Sharing did not move — it had never been built where this table already put it.** The owner,
looking at the Server services screen: *"aslında bir servisin private mi organization mu public mi
olduğu studio tarafında ayarlanacak."* That is this table's own row, restated back to us, which is
the clearest possible sign it had not been carried out. The layer editor had been built as **one
screen in Server holding all six pages**, so *Sharing* and *Cache lifetime* — two of the three
Studio rows about a layer — were implemented in Server, and a publisher had no route to either.

The repair is the split this table describes: `LAYER_PAGES` names the surface of each page,
the router sends `#/layer/x/sharing` to whichever surface owns it, and each editor's left column
lists its own pages **plus a link to the other's** — which is the second half of the sentence above,
and without it a publisher hunting for Sharing in Server finds four pages and no clue.

**Splitting it separated two buttons that had been sitting together as *Maintenance*.** *Delete
layer* is a decision about content — it purges tiles and forgets a shape, and the person who
published it unpublishes it — so it stayed in Studio. *Forget remembered shape* is a cache the
**server** keeps ([D-17](../architecture-debt.md)) and moved to General in Server. Neither had been
wrong before, because before there was one screen; the split is what made them different kinds of
act.

### 5d. The viewer belongs to Studio, and the directory links into it

`view.html` and `map.html` move under the Studio surface. They are content views — anybody who
can see a service can look at it — and the REST services directory's *View In* links point at
them, which is the handoff §4 found described in Esri's own documentation. The directory itself
stays where it is: it is a public face of the API (ADR-023), not part of either surface.

### 5e. The SDK's location becomes a setting, and the policy follows it

`Graticula:MapSdkUrl`, defaulting to the pinned `https://js.arcgis.com/4.29/`. The
Content-Security-Policy's `script-src`, `style-src`, `img-src`, `connect-src` and `font-src`
take their third-party origin **from that setting** rather than from a literal.

**A setting the policy does not follow is D-44 again**, exactly: an operator points the SDK at
their own host, the browser refuses it because the policy still names Esri's, and the page
renders with a dead map and no error the server can see. The two move together or the setting
is a trap.

### 5f. What the split needs that does not exist

**A content-scoped layer listing.** `/admin/layers` requires `admin:viewAllContent`, so Studio
cannot use it: a publisher would be refused the list of their own layers. Studio needs *what I
own, and what is shared with me*, which no endpoint answers today. It is built first, because
without it Studio is a shell — and it is the same shape as
[D-45](../architecture-debt.md): a listing that does not carry what a client needs to address
what it lists.

### 5g. What this is not

Not a new identity model, not a second session, not a per-surface API. Not a rewrite of the
existing screens: they move and are gated, and their code is the code that works today.

### 5h. Server's shape: a folder rail and one list of services

The owner, seeing the current Services screen while this was being built:
*"arcgis yapısına gidiyorsak, böyle bir yapıya da ihtiyacım yok."* They are right, and the
objection is structural rather than visual.

**Today that screen is three tables stacked**, and the primary unit is wrong. *Feature and
tile services* lists **layers**; *Services* lists services as a secondary table; *System
services* lists the geometry service as a third kind of thing. So one screen says a layer is
the unit, a service is a footnote, and a service with no layers is a separate species.

**The reference this is being taken from does one thing:** a **folder rail** on the left —
*Site (root)* and each folder, with a control to make one — and on the right the **services in
the selected folder**, one row each, with a search box over them and a publish action above.
Layers are not on that screen at all: a service is the unit, and you open one to see what is
inside it. Derived from ArcGIS Server Manager's *Manage Services*, which the owner supplied a
screenshot of; the structure is what transfers, not the pixels — no instance counts, because we
have no instance pool (ADR-031 §2a). *This sentence also said "no thumbnails", written before
the owner asked for them the same day; §5i is what replaced that half and it is kept visible
rather than edited away, because the reason given was that we have no renderer, and that turned
out to be an argument against copying their thumbnail rather than against having a picture.*

So Server's main screen becomes:

- **The folder rail**, from `GET /admin/folders`, including the root, each with what it holds.
  Selecting one filters the list. **New folder** is here, which is where it belongs — the rail
  is the only place folders are a subject rather than a field.
- **One list of services** in that folder: name, kind, status, sharing, how many layers and
  groups, owner, and the actions that act on a service — start, stop, delete when it is empty,
  and its settings.
- **The system services stop being a separate table.** The geometry service is a service in the
  `Utilities` folder, and now that folders are real it can be listed as one. Three tables become
  one list and a rail.
- **Layers appear when a service is opened** — *unless there is only one*. The owner, looking at
  the drill-in for a single-layer service, 2026-08-17: *"this is a really meaningless page tbh.
  we shall go to settings directly."* For a service of one layer and no groups that page is a
  one-row table whose only control is a **Settings** link, so the row now opens the layer and
  the drill-in is kept for the services where the list is a real choice. **The breadcrumb had to
  follow**, because it is the only way back when a step is skipped: it read *Services › hosted ›
  name*, where the middle word was `hosted` or `registered` — a fact about the data printed
  where a reader expects a place — and now reads *Services › folder › service › layer* with each
  step a link, and the service step present only when opening it would show something.
- **The map is not here.** It moves to Studio with the viewer (§5d): looking at data is a
  content act, and an operator watching a server does not need a map on the way.

**What is lost, and it is deliberate:** the flat all-layers table with its filter over name,
table, source and owner. It answered *where is this layer* in one screen, which a folder rail
plus a drill-in does not. The replacement for that question is search across services — the
reference has a search box over the same list for the same reason — and until it exists this is
a regression in one specific task, recorded rather than glossed over.

### 5i. Each row carries a picture and the actions that change it

The owner, on the same screenshot: *"this looks fancier"*, and then
*"bir de thumbnailler var. girmeden görebiliyoruz."* — there are thumbnails too, you can see it
without going in. Both are about the row rather than the screen, and they are two different
requests.

**The action strip is the plain half.** Their row carries share, start, stop and delete; ours
carried delete alone, and start/stop lived on the layer editor's General page — two screens away
from the status the list displays. So the list gains **Start/Stop** beside Delete. This is also
where a defect surfaced: stopping a service did nothing at all, and had not since migration 11
([D-57](../architecture-debt.md)). A control being far from the state it changes is part of why
that survived — nobody stopped a service while looking at a list of statuses.

**The picture is the half that cannot be copied, and the reason is architectural.** Theirs is a
rendered map image, because their server renders maps. Ours cannot: [ADR-004](ADR-004-rendering-engine.md)
is `DEFERRED` and [v1-scope](../v1-scope.md) puts WMS, MapServer, ImageServer and OGC Maps out of
scope, so **no path in this server turns geometry into pixels.** Two wrong answers were available:

- **A stock icon per geometry type** — a point/line/polygon glyph, the way their geoprocessing
  services get a toolbox picture. It is decoration: it says what the layer document's
  `geometryType` field already says, in a form you cannot read a number off. §6's rule against
  decoration that carries no signal rules it out.
- **Build a renderer for it.** A thumbnail is not a reason to un-defer ADR-004, and §82's
  question — *what concrete problem does this solve* — is answered better by the cheap version.

**So the browser draws it, from one query.** Up to 800 features of the service's first layer, at
`maxAllowableOffset` matched to the width of the box, `geometryPrecision=4`, painted with 2D
canvas calls. No SDK, no tiles, no renderer, and it works for a **registered** table as well as a
hosted one — which a tile-based preview could not, since tiles are hosted-only (Q-67). Verified
against both: `turkiye/tr_ref` is a registered layer and previews.

Three things about it are recorded because they are compromises rather than features:

- **It is a sample, and it says so.** 800 of `tr_yol`'s 46,041 features is 1.7%, and a dense line
  layer therefore looks sparser than it is. `exceededTransferLimit` from the same answer tells the
  hover text to say *of more*, so the picture never claims to be the layer.
  [D-58](../architecture-debt.md) carries what that costs.
- **Two hundred was tried first and was measured to be wrong.** The sample was never the problem —
  200 features of `tr_il` already spanned 75% of the layer's extent — the *density* was: two
  hundred two-vertex segments on an 80-pixel canvas reads as *this layer is nearly empty*. 800
  spans 96% at 115 KB and 34 ms; 2,000 costs 290 KB and 114 ms for no better picture.
- **The listing gained a field for it, and that field is the fix to a subtler bug.** A row needs
  one of its service's layers — to draw, and to address the status route, which is layer-scoped
  (see D-57). The console originally found one by walking the services directory, and **a stopped
  service answers 503 to that walk** — so the row for the one service somebody most wants to start
  was the row that could offer no Start button. `GET /admin/featureservices` now reports a `cover`:
  the lowest-numbered layer, present whether or not the service runs. Two tests, one of them
  asserting after a stop, because before a stop the broken version passed too.

The row therefore reads: preview, name over kind and what it holds, status, sharing, owner, and
the strip. The layer and group counts moved under the name and their own column was dropped —
they were in two places at once, and a table column is the wrong place for a fact that is
sometimes *"3 layers, 1 group"*.

### 5j. Making a thing is a New item dialog, not a drawer of forms — owner decision, 2026-08-19

*"ve bu ekran hiç güzel değil. bunu da kaldıralım. örnek olarak verdiğim arcgis ekranları daha
kullanışlı."* Then, when the first repair was still the wrong shape: *"ben ondan bahsetmedim. new layer
diyip öyle bir ekran istemiyorum. add item diyerek açılan ekranları da göndermiştim sana."* And then
the button itself, circled in red on the screenshot: *"1.si new item."*

**What was there.** A right-hand drawer titled *Create*, opened by the page action on both surfaces,
holding four forms stacked in one column: *Design a schema*, *Import a file*, *Publish a registered
table*, and *A service, and groups inside one*. Around 1,600 pixels of scroll, four headings, five
submit buttons, and nothing in the layout saying they were **alternatives** rather than steps. Its own
comment recorded the drift: *"Retitled 2026-08-16: it held only hosted layers, and now it also
publishes a table this server does not hold and creates services and groups. A heading that names one
of four things is worse than a general one."* A general heading was the wrong repair — the problem was
that one surface had four jobs.

#### The reference, in two screens

The owner sent both. Their *New item* dialog: a dashed drop zone across the top reading *Drag and drop
your file or choose an option* with a *Your device* button inside it, and beneath it a two-column grid
of item-type tiles — an icon square, a bold name, one sentence each. Clicking *Feature layer* opens a
second screen, *Create a feature layer*, which is a heading, the line *Select an option to create an
empty feature layer.*, five radio rows with a sentence each, and a footer carrying *Back* on the left
with *Cancel* and *Next* on the right.

So ours is three screens, and the third is the form:

| Screen | Asks | Footer |
| --- | --- | --- |
| **New item** | drop a file, or pick a kind of item | none — there is nothing to confirm yet |
| **Create a feature layer** | which of three routes | Back · Cancel · Next |
| the route's form | the fields that route needs | Back · Cancel, and the form's own submit |

The three routes are *Define your own layer*, *Publish a table this server can reach*, and *Upload a
file* — the first three drawer forms, unchanged in what they submit, because moving markup is not the
moment to also rewrite a contract that `ImportFormTests` asserts.

#### The entry point was the error, not the wizard

I read the first correction as a rejection of the radio-and-*Next* step and started rebuilding it as a
drawer of route rows — a row that is the choice, no confirmation, one interaction instead of two. That
was wrong twice over. The owner's objection was to beginning at *New layer*: you begin by adding an
**item**, and what kind of item it is comes second. And their second screenshot is the radio wizard
itself, sent as the thing they want. **Their shape wins on their own screens; my argument against the
extra press is recorded here and not acted on.**

#### The item that never exists — owner decision, 2026-08-19

Asked directly, after the owner walked the reference's upload flow and noticed it can add a file without
publishing it: **ours is the more correct arrangement.** *"evet bizimki daha doğru."* So the endpoint's
own note stands as a decision rather than as our reasoning — ArcGIS separates uploading an item from
publishing a service, which is right when items have a life of their own, and nothing here gives them
one. A file becomes a service or it becomes a refusal.

#### The fourth form leaves rather than moves

*A service, and groups inside one* is not on the dialog, and the reason is a rule the owner had already
given twice — *"add member shall be inside members section"*, and then *"grupun ve servisin orada
ilişkisi yok. servis katmanın bir özelliği."* A service is not an item you add; it is how a layer is
presented. So the two service forms keep the surface whose subject they are, **Server**, behind its own
*New service* action, and the two page actions stop being aliases for one drawer. Nothing is withdrawn:
`POST /admin/services` and the group-layer endpoints keep the route an operator can reach.

It also had a second problem worth naming, because it is a collision this product created for itself:
its *group* meant an ArcGIS **group layer**, three lines away from a sharing control, in a console that
now has **sharing groups** with four tabs of their own. Two unrelated things called the same word on one
screen. The group-layer form now says which it is in its own copy.

#### One tile where theirs has ten

Their grid offers URL, Application, Tile layer, Scene layer, Locator, Data store, Developer credentials
and a raster function template. This server has a feature layer. **The other nine are not drawn**, by
the same rule that kept a category rail off the group's Content tab and Categories, Tags and Location
off the item picker's rail in §5z: a control for a feature that does not exist is worse than its
absence, because it promises. The grid is two columns and laid out for the rest to arrive into.

#### And the drop zone is not decoration

It is the shortest path for the case the owner said matters — *"gdb import önemli"* — and it removes an
interaction rather than adding a surface: a file dropped there lands on the import form with the file
already attached and the layer name offered from the file name. The `File` cannot be assigned when it
arrives, because `#iFile` is on the third screen and does not exist yet, so it is held and written into
the input through a `DataTransfer` once the form is drawn. That is the only way to fill a file input
from script, and it keeps the form with one source of truth rather than two.

### 5k. Studio's item page has four tabs, and one of them is the layer list that was removed — owner decision, 2026-08-19

*"servisin özelliklerine girdiğimizde bu listenin gelmesi güzel."* And, on what an import of one
geodatabase produces: *"mesela o gdb içerisinde 20'den fazla katman var. hepsi hosted db'ye import
edildi, ve o servis adı altında publish edildi. yani db'den direkt servis publish edilmeyecek. servis ve
katman ayrı şeyler. bir serviste n katman olabilir."*

**It is Studio's page, and getting that wrong cost a build.** Every screenshot the owner sent is an
ArcGIS Online **item page** — the thing a publisher opens from their own content — and I built the four
tabs on the service page without noticing that page renders on both surfaces, so the structure landed on
**Server**, which is the administrative surface and was never what those screens showed. The owner's
correction was flat: *"sana verdiğim ekranlar studio'dan. sen gidip server'ı değiştiriyorsun… server
ekranlarını da geri al."*

So Server's service page is back to what it was — Capabilities and Limits, drawn directly, no strip, no
Overview, no Data, no delete — and the four tabs, the layer list, the address column and the delete lock
are Studio's. Which surface owns which *settings* page is untouched: §5c and `SERVICE_PAGES` still put
Sharing in Studio and the ceilings on Server.

**This reverses §5's own note of 2026-08-18, and the reversal is the record.** The service page's layer
list was removed then, by the same owner, on the grounds that the page is the service's settings and the
counts are in the facts line. What came back with it is a fact nobody had yet: a geodatabase import
makes **one service with twenty-odd layers**, and a page that says *23 entries in the service document*
and lists none of them is a page you cannot work from. The earlier decision was right about a table
that repeated the document; it was made before there was a service worth listing.

**A service is not a layer, and the import must stop behaving as though it were.** Every route into
hosted data today makes one service holding one layer, so the two words have been interchangeable in
practice. They are not: `hosted/Environmental_gdb` will be one service, its twenty-three feature classes
its layers, each its own table in the datastore. The mechanism already exists — *Into service* on the
registered-table form adds a layer to a named service at the next free index — and what is missing is
the geodatabase import using it.

#### The four tabs, from the owner's screenshots of the reference's item page

| Tab | What it is | Ours today |
| --- | --- | --- |
| **Overview** | the service's own facts, **the list of layers in it**, and the right-hand column carrying the service's address | facts line only, list removed 2026-08-18 |
| **Data** | a layer picker, then its rows — and a **Fields** view of its columns | nothing |
| **Visualization** | the service on a map. *"dümdüz"* | **built 2026-08-19** — see below |
| **Settings** | for now, **delete** — with an accidental-deletion lock and a confirmation | delete is elsewhere |

**Data shows the geometry column and theirs does not, by owner decision:** *"coğrafi kolonlar özellikle
gizlenmiş ama bizde açık olabilir çok sorun değil."* Their table hides it; ours may show it.

**Fields may delete a column, and only on hosted data.** *"field ekranında kolon seçip silebiliyoruz. bu
hosted olanlarda kabul edilebilen bir özellik. reference registered olanlarda planlama yaparız."* The
distinction is the one this repository already draws everywhere else and for the same reason: a hosted
table is ours to alter, and a registered one points at somebody else's database and must never be
touched. So dropping a column is offered on hosted layers and the registered case is deferred rather
than refused-by-accident.

**Visualization absorbs two existing screens rather than adding a third.** *"bizdeki map, tiles vs
ekranlarını kaldırıp buraya alalım."* The Map action on a content row and the tile screens go; what
replaces them is one place where a service is looked at.

#### How it is built, and the two things the design review corrected first

`#mapPanel` moved out of Studio's content screen into this tab. **Three** controls used to open it, not
two — the review found the third: the layer editor's own *Show on map* / *Show tiles*, which exist on
both surfaces. All three navigate here now, so nothing opens a map of its own.

- **One layer at a time, through a picker shaped like Data's.** Not a preference: `clearMap()` has
  replaced rather than accumulated since 2026-08-16, on the recorded grounds that *the question a viewer
  answers is what does this layer look like, which has one subject*. A 55-layer legend is the opposite of
  *dümdüz*.
- **Features and Tiles are two modes of one control**, in the same two-item strip Data uses for Table and
  Fields, because they are two renderings of one subject. Tiles is offered only on a hosted service; a
  registered layer has no vector tile service and the mode would answer 404.
- **The legend collapses to one caption line** under the map — swatch, layer, `EPSG:3857` — since there is
  only ever one subject.
- **The basemap bar sits above the map**, not below it as the panel had it: it governs what you are about
  to see rather than reporting what you are seeing. It stays a browser preference in `localStorage`,
  which is ADR-020 §2's line — no server capability is added.
- **The tab says it is working.** The old panel showed its empty-state placeholder from the moment a
  layer was picked until the SDK either painted or gave up fifteen seconds later. *Dümdüz* still means
  saying something is happening.
- **The tab is absent when there is nothing to draw**, by the rule Data already uses, so a system service
  keeps its one-tab page.

**The address carries the tab, the layer and the mode** — `?tab=visualization&layer=0&mode=tiles` — and
that is the router's first query string. It exists because this function splits the hash on `/` before a
service's `folder/name` is reassembled, so a tab cannot go in a path segment; a query sits outside the
split. It also lets the layer editor's buttons cross from Server to Studio, which a module variable
cannot survive.

**Two mistakes on the way, both measured rather than reasoned.** The first draw of the tab strip happens
before the FeatureServer document arrives, so Visualization does not exist yet and a `?tab=` asked for
then was filtered out and lost — the request is now held and granted on the draw that can. And
`drawServiceVis` re-read `?mode=` on every redraw, so pressing *Tiles* set the mode and the next draw put
it back: an address is an instruction on arrival, not a standing one.

#### Deleting a hosted service takes its data with it — owner instruction, 2026-08-19

*"yanlış ifade. servis hosted sa ve silindiyse, datastore dan silinmesi lazım."* The delete panel said
the opposite — *the tables in the datastore are not dropped* — and that sentence was accurate about what
the server does and wrong about what it should do.

**This reverses a deliberate refusal, and the refusal is worth naming before it goes.**
`DELETE /admin/featureservices/{name}` answers *"'{name}' still holds N layers. Deleting a service does
not delete what is in it — unpublish the layers first"*, and `DELETE /admin/layers/{name}` removes the
registration and says *the table in the data source was not touched*. Between them there is **no route
that drops a hosted table at all**: an operator who imports a geodatabase and changes their mind is left
with fifty-five tables and a database client.

**The hosted / registered line is what makes it safe, and it is the line this whole product is built
on.** A hosted table is ours — created by `PostGisImporter`, named by us, owned by the service. A
registered one points at somebody else's database and must never be touched. So:

| the layer is | deleting the service does |
| --- | --- |
| hosted | unpublishes it **and drops its table** |
| registered | unpublishes it, and the table is left exactly as it was |
| a group layer | removes it; it holds no data |

**The guards are the two the owner already asked for, and they are now load-bearing rather than
polite.** The lock starts closed and has to be cleared; the confirmation names the service, the number of
layers and — this is the part the copy was getting wrong — **how many tables will be dropped**. A
confirmation that says *are you sure* in front of an irreversible drop is a confirmation that has not
said anything.

**And the response says what happened per layer**, because a service holding one hosted and one
registered layer does two different things, and a single *deleted* would hide the half that was left
alone. Same reasoning as the geodatabase import's per-layer report in
[ADR-038](ADR-038-how-a-geodatabase-becomes-a-service.md) §5.

**Settings gets delete, and delete gets two guards.** *"yanlışlıkla kullanıcının silme durumu
engellensin. silerken de emin misin diye sorarız."* A checkbox that prevents accidental deletion —
their *Prevent this item from being accidentally deleted* — and a confirmation that names what goes.
This is the second time this month a destructive action has been asked to carry more friction than a
harmless one; [D-97](../architecture-debt.md)'s neighbours in the 2026-08-19 review found the same
imbalance on roles and members.

#### A row opens the item, and the item lists its layers — corrected 2026-08-19

*"tıkladığım feature layer'in özellikleri direkt böyle açılacak. burada da servisin url'i var."*

**This corrects something I built hours earlier on the same day.** [D-98](../architecture-debt.md) found
the layer editor unreachable by any click, and the repair I chose was to make a single-layer content row
open its layer directly. The owner's screenshot says the row opens the **item page** — Overview, with
the layer list — and each layer is entered from there. Which is now possible precisely because Overview
exists: the shortcut was solving a problem the layer list solves, and it disagreed with the reference
while doing it. So the shortcut goes and the route stays.

**Overview gains the right-hand column their item page has**, and one thing in it is worth more than the
rest: **the service's URL, with a copy button and a link that opens it.** An operator wiring a service
into anything — ArcGIS Pro, a web map, a script — needs that string, and today this console shows it
nowhere. What else goes there is what we already know and currently scatter: the kind, the owner, the
folder, the sharing scope, and the counts.

**What is deliberately not copied from it.** Their column carries an *Item Information* completeness
meter, a star rating, Categories, Tags, Credits and a Metadata button. None of those exist here: there
is no item store (§5j), no rating, no tag or category storage, and no metadata document. A meter scoring
a description we do not store would score nothing.

#### What this does not decide

**There is still no item that exists without serving.** §5j recorded the owner's answer on that —
*"evet bizimki daha doğru"*, ours is the more correct arrangement — so a service page is the page for
a thing that is already published, and the reference's *Add item without publishing* stays out.

### 5l. Sharing is set from the item, not only from the group — owner decision, 2026-08-19

*"sharing kısmına gelirsek, şu düğmeler. tıklanınca açılan 2. görüntü. edit group sharing diyince de,
üyesi olduğum gruplarla o nesneyi paylaşabilme seçeneği geliyor."*

**Both directions exist in the reference and only one exists here.** A group's Content tab can add items
— built 2026-08-18, the picker with thumbnails and a counted select-all. What is missing is the other
way round: standing on an item and choosing which of your groups it goes to. Today that means leaving
the item, opening each group, and adding it there.

#### Three screens, from the owner's screenshots

| | What it holds |
| --- | --- |
| **the row's control** | a small button per content row, showing at a glance who can reach the item |
| **Share** | *Set sharing level* — three radio rows, Owner / Organization / Everyone (public), each with a glyph and a sentence — then *Set group sharing*, the groups it is already in, with **Remove** and **Edit group sharing** |
| **Group sharing** | search, filters, `Selected: 2`, `1-54 of 54`, and a checked list of **the groups you are a member of**, each row saying `1/1 items already shared` |

**The scope list is ours already.** Owner / Organization / Everyone are `private` / `organization` /
`public` — the same three §5z's content scopes are computed from, and each already has a glyph that
renders (`ICONS.private`, `.organization`, `.public`) so the dialog needs no new vocabulary.

**The endpoints are all there too.** `PUT /admin/services/{name}/sharing` sets the scope;
`PUT`/`DELETE /admin/groups/{name}/items/{service}` add and remove a group. So this is a console screen
over a surface that already works, which is the cheapest kind of screen to be missing and the easiest
kind to leave missing.

#### What is *not* there, and it is a privacy decision rather than a field

The content listing carries `throughGroups`, and it answers **how this reached you** — it is populated
only when the scope is `group`, because that is what §5z needs it for. The Share dialog needs a different
fact: **every group this item is shared with**, whatever the reason you can see it.

`layers.ListServicesAsync` already returns `SharedWith`, and the endpoint filters it. Exposing it
unfiltered would tell any reader who can see an item the names of every group it is in — including
groups they are not in, which is a list of other people's teams. **So it is returned only to a caller
who may change the sharing**: the owner, or an administrator holding `admin:manageAllContent`. Anybody
else gets what they get today, which is why they can see it and nothing about who else can.

That is [ADR-018](ADR-018-authorization-and-roles.md)'s reasoning applied one level down: a scope is
public information about an item, and the *set of groups* is information about the groups.

#### And the group list is the caller's own

*"üyesi olduğum gruplarla"* — the groups you are a member of. `/admin/groups` already answers with
`standing` and `contribute` per row, so the list is filterable to the ones the caller may actually share
into: a group whose `contribute` is `managers` and where your standing is `member` is a group you cannot
add to, and offering it would be a control that fails on press.

**Their row annotation is worth copying and is not free.** `1/1 items already shared` tells you, before
you tick, that this group already has it. We can say the same thing from `SharedWith` for one item; the
reference is counting across a multi-item selection, which our picker does from the group's side and
this dialog does not need.

### 5z. Content is listed by how it reached you — owner decision, 2026-08-18

*"content can be my own, from my groups, or shared in organization. I think we need a public section as
well to get publicly shared items."* And, on the item picker built the same day: *"going with name only
is not feasible. I need to see thumbnail etc for items. I also need to see the thumbnails in studio
content."*

**The four sections are one value the server already computes.** `LayerAccess.Evaluate` returns a
`Reason` — Owner, Group, Organization, Public, plus AdministrativeOverride — because
[ADR-018](ADR-018-authorization-and-roles.md) condition 3 wanted an auditable answer to *why could they
see this* rather than a boolean. The console had been receiving that on every row and throwing it into
two buckets called `mine` and `shared`.

#### But the reason cannot be the label, and using it that way was measured wrong before it shipped

`Evaluate` tests `Public` **before** ownership, which is right for the question it answers: a public
service is readable whoever asks, and the cheapest sufficient justification is the correct audit
answer. Used as a content scope it filed **ten of this server's eleven services under *public* for the
person who owns all of them** — so *My content* would have shown one item to an operator who published
every one.

So ownership decides the section and the reason is reported beside it as `because`, which is the fact
that says who *else* can see the thing. Recorded rather than fixed in `Evaluate`: changing that
precedence would make an audit line say *they read it because they own it* where *because it is public
to everybody* is the stronger fact.

| Scope | Rule |
|---|---|
| **Everything** | all visible — **the default** |
| **Mine** | `owner == me`, whatever its sharing |
| **From my groups** | not mine, reached through a group you are in, **naming which** |
| **Organization** | not mine, shared with every signed-in member |
| **Public** | not mine, readable by anybody |
| **Not shared with you** | not mine, private to its owner, visible by administrative override |

**Everything is the default and that is a first-run decision.** Four of five scopes are empty for a new
operator and the fifth holds everything; defaulting to *Mine* hands them a blank screen with the
content one unclicked tab away, which is the failure this console already shipped on the Groups screen.

**Each scope is an address** — `#/content/organization` — because five sections whose only handle is a
click are five things you cannot send anybody to.

#### The picture is drawn from the data, and that constrains the page size

There are no stored thumbnails and no file storage. `preview.js` draws a service's geometry from one
query, and its own header already refused the alternative: *"a stock icon per geometry type would be
decoration rather than information, and this project's rule is that a picture of the data has to come
from the data."*

**So the page is ten, and the reference's sixty is the thing not to copy.** Measured: a dense line
layer is about 115 KB, and ten rows of the Services screen cost 612 KB over 510 ms. Sixty rows a page
would be roughly 120 requests and over two megabytes *per page*. At ten, the mechanism that already
exists — `paintPreviews` walking the page in DOM order, awaiting each — is the whole answer.

#### The item picker is a page, and the rule was already written in this repository

`#/group/{name}/add`. [D-83](../architecture-debt.md) is the `<select>` it replaces. Not a modal: this
file's own note beside the issued-password dialog says a modal is for *content that must not be left on
screen*, and a panel for *a decision an operator may want to read the page behind*. Choosing what to
share is the second. A page also makes focus return, Escape and scroll containment the browser's
problem rather than ours, and gives Back and copy-link for free.

**Six things about multi-select, decided rather than inferred**, because this console has twice shipped
a control that could not be operated: the selection outlives paging and filtering; select-all means the
filtered set and its label carries the number (*Select all 4 matching*); indeterminate means some-but-
not-all of that set; the footer counts the selection and names what is off-page; a service already in
the group is shown **ticked and disabled** and labelled, not filtered out, so somebody hunting for it is
told rather than left to share it twice; and a partial write reports per item, leaving the refused
selected so retry is one press.

**And one absence of the reference's that must not become ours.** Their dialog says nothing about
whether an item will actually reach the group's members. Ours must, because sharing into a group and
setting the service's own scope are two acts and either alone reads as done — so each row shows its
current scope, and the confirmation counts how many of the added items still reach nobody.

#### What is deliberately not built

Their rail carries Categories, Tags, Location and Date created. **Nothing here stores any of them**, and
a filter that filters nothing is worse than no filter — the same rule that kept a category rail off the
group's Content tab. Their item-type tree has twelve branches against our three service kinds, and
`kind` has one value on this server; it arrives as a flat filter when it has a second. Folders **are**
built, because folders are the part of that rail we have data for and they are what makes five hundred
items navigable.

## 6. Consequences

- **ADR-020 is amended.** Its §5e table of surfaces becomes Server's; Studio is new.
- **The two environments differ by hue, in two places that agree.** Owner decision 2026-08-18:
  Studio's navigation column is not Server's colour. Server is indigo `#171b49` and Studio is violet
  `#281748` — within a few points of each other for lightness and about 50° apart in hue, so they
  read as one product at two stations rather than as two applications. **The choice was not free:**
  the environment switch has been teal for Server and violet for Studio since §5a, so a violet column
  makes the hue say the same thing in both places instead of two things in two places, and a reader
  who learns one gets the other for nothing. The surface is written to the root element by the router
  and every rule that depends on it says so in the stylesheet. **The sidebar illustration does not
  change**, because it is the product's subject and not the environment's badge.
- **The console's URLs change**, and bookmarks to `#/services` and `#/layer/…` break. Said out
  loud because ADR-020 §5c took *frozen URLs* from the reference as a rule, and this breaks it
  once, deliberately, before there is anybody to break it for. The old roots redirect.
- **The anonymous view moved to Studio**, and this bullet used to say the opposite — *"stays in
  Server, which is right and slightly sad"*. The owner, 2026-08-17: *"no anonymous view for
  server."* They are right and the original reasoning was already leaning that way: the screen
  answers *what does a stranger see of this layer*, which is a question about content and its
  sharing rather than about a running server. It stayed in Server because that is where it was
  written and because it read `/admin/layers`, which needs `admin:viewAllContent`. It now reads
  `/content/layers` — the publisher's own listing — so the surface and the privilege agree.
  The per-layer version this bullet promised is still owed; the tab is the whole-content form of
  it.
- **Q-112 (groups)**, when answered, lands in Studio.

## 7. Conditions

1. **No screen appears that its reader cannot use**, asserted by a test that signs in without
   `admin:manageServer` and finds no Server surface — not a hidden one, not a 403 toast. The
   current console's per-section failure isolation makes the opposite failure invisible, which
   is why this is a test and not a review note.
   **The owner restated this condition on 2026-08-17 as a requirement:** *"admin olmayan
   kullanıcılar şuradaki server studio ayrımını görmeyecek bile. sadece studio onlarda olacak.
   server ekranına gitse bile ayarlama yapamayacak."* — a non-administrator will not even see the
   Server/Studio distinction; they will have Studio only; and going to the Server screen will not
   let them change anything. **All three are written and none is measured.** The switch is hidden
   when fewer than two surfaces are allowed (`drawSurfaces`), `/server/` redirects to Studio with a
   sentence naming the missing privilege (`route`), and every write behind it is gated at the
   endpoint. Restating a requirement is not evidence that it holds, and this remains the one
   condition whose evidence is unobtainable.
   **PARTLY DISCHARGED 2026-08-17, and the part that is not is honest about why.** D-56 closed:
   `POST /admin/members` creates a member with a role, a user type and a first password, so the
   reader this condition needs now exists. **Measured with one:** a `publisher`/`creator` account
   holds seven privileges and neither `admin:manageServer` nor `content:registerDataStore`; every
   `/admin` route it touched refused it (403 × 8, `/admin/health` 200 because it is deliberately
   anonymous and redacted — D-18); `/content/layers` answered with 0 owned and 12 shared; and a
   browser holding that account's token, asked for `/server/#/services`, landed in Studio with
   `esra · publisher · creator` in the header and *My content* selected.

   **The first thing that measurement did was falsify a claim in this ADR.** The Server/Studio
   switch was **visible** to the publisher, although `drawSurfaces` sets `hidden` on it: the
   attribute's rule is user-agent weight and `#surfaces { display: flex }` beats it. So the
   sentence *the switch is hidden below two allowed surfaces* — written here and in a commit
   message the same day, from reading the code — was false for as long as it had been written.
   Fixed as `[hidden] { display: none !important }`, recorded as [D-46](../architecture-debt.md)
   instance 9, and re-measured: the publisher's header now carries no switch and no health line,
   the administrator's carries both.

   **A test now holds it, 2026-08-18, and it fails a build.**
   `SurfaceTests.Without_admin_manageServer_there_is_no_Server_surface_to_see` in
   `tests/Graticula.Console.Tests` asks for `/server/#/services` as a reader who does not hold the
   privilege, requires the address to end up under `/studio/`, and then asks the browser what it
   painted — `offsetParent === null` and a computed `display` of `none` on `#surfaces`. **The
   rendering question rather than the attribute question, deliberately**, because the attribute
   question is exactly what was asked before and it answered *hidden* about a visible element. It
   was verified by putting the CSS defect back: with `[hidden] { display: none !important }`
   removed, the test fails at that assertion with the sentence naming D-46 #9.

   **The remaining half is now buildable, 2026-08-18.** Q-116 is answered — ADR-015 §6c — and
   `DELETE /admin/members/{name}` exists, so a suite can create the publisher this condition needs
   and remove them afterwards without leaving an account behind. The condition is not discharged
   here because the test has not been rewritten to do that: the console suite still edits
   `admin:manageServer` out of the server's own `/rest/whoami`, which asks the console the real
   question with a synthetic reader. What changed is that the blocker was *there is no way to clean
   up* and now it is *the test has not been changed*.

   **Still PARTLY DISCHARGED, and the remaining half is one sentence long.** This condition says
   *a test that signs in without `admin:manageServer`*, and the test signs in as an administrator
   and removes that privilege from the server's own `/rest/whoami` answer on the way back to the
   page. The shape stays the server's, so the console is asked the real question — but the reader
   is synthetic. The honest reason is small and worth stating: there is no `DELETE /admin/members`,
   so a suite that created a publisher would leave a live publishable account behind on every run,
   on the owner's server as well as in CI. Whether members should be removable is
   [Q-116](../open-questions.md); when it is answered one way, this condition can be discharged in
   its own words, and the measurement with the real `publisher`/`creator` account above stands as
   the evidence until then.

   **What is still not discharged is the test.** All of the above is a person running a script and
   looking at a screenshot; the condition asks for something that fails a build. That needs a
   browser harness, which this repository has none of — [D-59](../architecture-debt.md) — and it
   is the same absence that let the switch claim stand for a day. **Marked `PARTLY DISCHARGED`
   rather than done, because the difference between *measured once* and *asserted on every push*
   is exactly what this session kept paying for.**
2. **One stylesheet and one map module across both surfaces**, asserted by a test that fails
   if a second copy of either appears. D-46 has six recorded instances and this decision
   doubles the opportunity.
3. **The SDK setting and the Content-Security-Policy move together**, asserted by a test that
   sets a different SDK origin and checks the policy names it. Otherwise §5e is a trap rather
   than a setting.
4. **The content listing exists before Studio claims to list content**, and it reports
   ownership rather than implying it: *mine* and *shared with me* are different answers and a
   publisher acts differently on each.
5. **Moving between the surfaces costs one action for a reader who may be in both**, checked by
   using it rather than by reading it. An administrator is also a publisher, and a split that
   makes them navigate twice for one layer will be resented by the person it was not built for.

## 8. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-079 | The publisher and the operator are different people often enough for two surfaces to be worth their cost | `UNVALIDATED`, and on a small deployment they are the same person, which is the case this decision serves worst. The owner's own estate is the first place to check it |
| A-080 | `admin:manageServer` is the right single gate for Server, so no reader is left without a tool they need | `UNVALIDATED` but cheap to falsify: it fails the first time somebody has to ask an administrator for something about their own layer, and that complaint names the screen that is in the wrong surface |
