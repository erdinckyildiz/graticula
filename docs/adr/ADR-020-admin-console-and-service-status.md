# ADR-020 — An admin console, and what "stop a service" means

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM-HIGH` for the console · `MEDIUM` for the status model |
| **Decided** | 2026-08-14 |
| **Answers** | [ADR-017](ADR-017-admin-api.md) §7's deferred *whether there is a web console* · owner request for start/stop |


> **Amended 2026-08-17 by [ADR-034](ADR-034-server-and-studio.md).** One console becomes two
> surfaces over the same API: **Server** for the operator, gated on `admin:manageServer`, and
> **Studio** for the publisher, gated on being signed in. Everything below still describes the
> screens; what changes is which surface each is in and who is shown it. The reason is a defect
> in what this ADR built: §5e's per-section failure isolation was designed for a half-broken
> *server* and, against a half-privileged *reader*, produces a console whose screens refuse one
> at a time. §5c of ADR-034 has the allocation, derived from the privileges each endpoint
> already demands rather than from where a screen currently sits.

---

## 1. Context

Owner request: a UI that lists services, visualises one, and can **start and stop
them**.

Two of those three are a client of endpoints that already exist. The third is
not — nothing in this server has a notion of a service being *stopped*, and the
obvious candidate for it, ADR-018's sharing scope, is a different thing wearing
a similar shape. §3 separates them.

[ADR-017](ADR-017-admin-api.md) §7 deferred *whether there is a web console*,
with the reasoning that ours is an API-first decision and a UI, if any, is a
client of it. That reasoning survives; the deferral does not.

---

## 2. Decision — there is a console, and it is a client with no back door

**It ships with the server, served from the same process** — at `/console` when this was written, and at `/server` and `/studio` since [ADR-034](ADR-034-server-and-studio.md) split the audiences.

The alternative — a separate deployable, as Honua does with `honua-console` — is
the right shape for a product with several servers under one UI, and the wrong
one for a product whose whole positioning is *one deployable against one
PostgreSQL* ([ADR-019](ADR-019-portal-server-split.md)). Telling somebody who
cannot afford ArcGIS Enterprise that the admin UI is a second thing to install
and keep in version lockstep is exactly the friction this product exists to
remove.

**The binding constraint, and it is the whole of ADR-017 §7's reasoning
preserved:**

> The console may use **no capability the admin API does not expose**. It
> authenticates the same way, holds the same session token, and is refused by
> the same privilege checks.

A console with a privileged path into the process would make the API the second
interface rather than the only one, and every capability would then exist twice
— once well, once as whatever the UI needed that week. Concretely: it is static
files and `fetch`. It gets no service registration, no direct database access,
and no endpoint of its own.

**Consequence, accepted:** anything the console can do, a script can do, which is
the point. Anything it cannot do is a gap in the API rather than in the UI.

---

## 3. Decision — a service has a status, and it is not its sharing scope

**New state: a layer is `started` or `stopped`.**

This is the ArcGIS Server concept, and it is genuinely distinct from ADR-018's
sharing:

| | Sharing scope | Status |
|---|---|---|
| Question | *Who may see this?* | *Does it run at all?* |
| Axis | authorization | operations |
| Stopped/private affects | one audience | everyone, including the owner and administrators |
| Set by | the item's owner | an operator |
| Typical reason | this is a draft | the source table is being rebuilt |

**Why not reuse `private`.** Making a service unavailable by marking it private
would hide it from everyone except its owner and administrators — who would
then still hit a source that is mid-rebuild. Worse, it silently changes an
authorization fact to achieve an operational one, and the sharing scope it
overwrote is gone: restoring the service afterwards means remembering what it
used to be. Two concepts, two columns.

**A stopped service answers 503, not 404.** It exists and is not available,
which is a different sentence from *no such layer*, and an operator restarting
a client needs to see the difference. It is listed in the catalogue for a caller
who may administer, and hidden from one who may not — because to a data consumer
a stopped service is indistinguishable from an absent one, and saying otherwise
leaks the inventory.

**Default `started`.** A layer that has just been published is one somebody
wanted; requiring a second call to turn it on would make publishing a two-step
operation for no benefit.

**Stopping requires `admin:manageServer`**, not the publisher privilege.
Publishing is a content act; stopping a running service is an operational one
that affects every consumer of it, including people the publisher has never met.

---

## 4. Decision — the map library is not Esri's proprietary SDK

**Amended 2026-08-14, the same day, on a corrected premise.** This section first
chose esri-leaflet over the ArcGIS Maps SDK on redistribution grounds. That
reasoning was too broad: **loading the SDK from Esri's CDN is not
redistribution** — the browser fetches it and nothing proprietary ships inside
this product. The objection only ever applied to bundling it.

So the console uses the **ArcGIS Maps SDK**, and the argument for it is stronger
than neutrality: esri-leaflet is a light client that asks for very little, and
the Maps SDK is what ArcGIS Pro and every Esri web app are built on. Pointing the
demanding client at ourselves is the closest available test of whether this
server is ArcGIS-compatible or merely compatible with the subset we implemented —
and the first time it was tried it found four parameters we refused that every
client sends.

**What still holds:** nothing proprietary may be vendored into the repository or
the image. If the SDK is ever bundled for an air-gapped deployment (Q-15), this
decision is reopened, and esri-leaflet — which is Apache-2.0 — is the fallback
that needs no permission.

#### 4a. The basemap half is reversed — 2026-08-16

This section also chose **OpenStreetMap's public tiles** as the basemap, on the
reasoning that Esri's own basemaps need an API key tied to an ArcGIS account and
the people this product is for do not have one. **That was right about Esri and
wrong about OpenStreetMap.** Their tile servers are volunteer-run and their usage
policy does not permit being an application's basemap. On 2026-08-16 every tile in
the console came back **403 Access blocked**, which is that policy being enforced
rather than a fault to route around.

**The likely trigger was one of our own security headers**, which is the third time
in one day that shape appeared. `Referrer-Policy: no-referrer` is set for a good
reason — an ArcGIS token can be in a URL — and it leaves every tile request with no
`Referer`, which is what OpenStreetMap blocks. Weakening the header to satisfy a
third party would be the wrong trade, and it would not make the use legitimate
anyway.

**So no basemap ships.** Layers draw on a plain ground, which is all this console
asks of a map, and an operator with a tile server of their own can name a template.
That setting lives in the browser rather than the platform store, because it is a
preference of the person reading and not state of the server — so §2's constraint is
untouched: it adds no capability the admin API lacks, because it adds no server
capability at all.

**Two things this improves beyond the licence question.** An air-gapped deployment
(Q-15) now has a console that works rather than one full of failed tile requests;
and the default install makes no third-party request at all, which is what the
`Content-Security-Policy` in §5f already wanted to be able to say.

**And a ground ships, because no basemap at all was the wrong answer too.** The
owner's objection was immediate and right: geometry floating in white is not a map,
and worse, *being in the wrong place* and *having no data* look identical — which is
exactly the confusion that followed. So the console carries **Natural Earth's
1:110m land outline**: 87 KB of GeoJSON, **public domain**, served from this origin.

Three reasons that is the right shape rather than a compromise. It is **data, not a
service**, so no usage policy governs it and nothing can start returning 403. It
costs **no third-party request**, so the air-gapped case and the tight policy both
hold. And it is **coarse on purpose** — it answers *where in the world is this* and
nothing else, which is the only question a ground has to answer here. Recorded in
[DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md) as the one piece of
third-party content shipped inside the product.

A configured tile template still replaces it, for anyone who has a basemap they are
entitled to use.

**The SDK half stands.** It is still the ArcGIS Maps SDK, still from Esri's CDN,
still the demanding real client — and it is now fetched on demand rather than in a
blocking tag, so a machine that cannot reach it loses the map and keeps the console.

The original comparison, retained:

| | ArcGIS Maps SDK for JS | esri-leaflet |
|---|---|---|
| Licence | Esri proprietary terms | **Apache-2.0** |
| Made by | Esri | Esri |
| Reads our FeatureServer | yes | yes |

**`VERIFY` — the precise terms of the ArcGIS Maps SDK for JavaScript are not
confirmed here**, and D-06 already records that dependency licensing is
deliberately unexamined. What is certain is that it is not open source, and that
matters twice over: ~~this product is Apache-2.0 and~~ **this product is Elastic
License 2.0 — corrected 2026-08-25,
[ADR-047](ADR-047-the-outbound-licence-is-elastic-2.md) — and still** must be
redistributable by
anyone who forks it, and its users are specifically people who **cannot or will
not enter into Esri licensing** ([product-context.md](../product-context.md)).
Shipping an admin console that requires them to is close to self-defeating.

**Where the proprietary SDK is still the right tool: verification.** Proving
that ArcGIS Pro and the Esri JS SDK can consume this server is a compatibility
claim worth testing, and testing with it entails no redistribution. That is a
test-harness use and is not this decision.

---

## 5. What the console shows, and why each screen exists

Derived from ADR-017 §3's scenarios rather than from the nouns, for the same
reason that ADR gave:

| Screen | Answers | Endpoint |
|---|---|---|
| **Services** | What exists, who can see it, is it running | `GET /admin/layers` |
| **Service detail** | Where is it, what fields, does it draw | the layer document plus `query` |
| **Start / stop** | Take it out of rotation without deleting it | `POST /admin/layers/{n}/start` and `/stop` |
| **Sharing** | Who may read it | `PUT /admin/layers/{n}/sharing` |
| **Data sources** | Where does this come from, can we still reach it | `GET /admin/datasources` and `/capability` |
| **Health** | Is the platform store up | `GET /admin/health` |
| **New hosted layer** | Design a schema, or import a file | `POST /admin/hosted/define` and `/import` |
| **Audit** | Who changed what | *owed — see §7* |

---

### 5e. Rebuilt 2026-08-16 — what §5's table looks like now

§5b's finding was that three sections against a twenty-route API is not a
consequence of choosing one deployable. The console was rebuilt on that finding.
**No endpoint was added**: everything below was already being returned and
discarded.

| Surface | Screen | Answers | Endpoint |
|---|---|---|---|
| **Services** | Layer list | What exists, where it lives, is it running, who can see it, what table is under it, who owns it | `GET /admin/layers` |
| | Layer settings page | All of the above per layer, plus every operation that acts on it. A drawer until 2026-08-17, then its own page at `#/layer/<name>/<page>` — §5i | below |
| | · show / tiles | Does it draw, and does a real Esri client accept our tiles | the service itself |
| | · start / stop | Take it out of rotation without deleting it | `POST /admin/layers/{n}/start`, `/stop` |
| | · sharing | Who may read it | `PUT /admin/layers/{n}/sharing` |
| | · cache lifetime | How stale a tile may be, set by whoever knows the data | `PUT /admin/layers/{n}/cache` |
| | · style | What it looks like, and back to generated | `GET/PUT/DELETE /admin/services/{n}/style` |
| | · refresh | Forget the remembered table shape after altering it at the source | `POST /admin/layers/{n}/refresh` |
| | · delete | Remove the publication | `DELETE /admin/layers/{n}` |
| | System services | Services with no layer, and their own sharing scope — ADR-018 §3b-i | `GET /admin/services`, `PUT …/sharing` |
| **Data sources** | List | Where layers read from | `GET /admin/datasources` |
| | Probe | Does the credential still work, what can it publish — read now, not remembered | `GET /admin/datasources/{id}/capability` |
| | Register | Test, then store | `POST /admin/datasources/test`, `POST /admin/datasources` |
| **Operations** | Platform store | Is the catalogue reachable, how many layers | `GET /admin/health` |
| | Runtime | Uptime, heap, allocation, **GC pause as a share of wall-clock** | same |
| | Caches | Tiles on disk, shapes remembered and for how long | same |
| | Route governance | Every route and what governs it, and **the ungoverned count** — ADR-018 condition 5 checked rather than asserted | `GET /admin/routes` |
| — | New layer | Design a schema, or import a file | `POST /admin/hosted/define`, `/import` |
| — | Audit | Who changed what | *still owed — §7* |

**Built against captured payloads, not against assumption.** The server was
started, seeded and every one of the fifteen requests this console makes was run
against it before the screens were written. That found three places where the
response was not the shape assumed, and one that would have been a trap: the style
endpoint answers **either** the stored document byte-for-byte **or** a wrapper
saying none is stored, and a console that fills its editor with the wrapper invites
somebody to store the wrapper as their style.

**Two gaps in the API, named here rather than worked around in the UI**, which is
what §2's constraint is for:

1. **The layer listing does not carry `cacheSeconds`.** So the cache box sets a
   value it cannot show. The screen says so out loud rather than showing an empty
   field that reads as *no lifetime set*.
2. **Nothing exposes jobs, audit or per-request observability.** §5b found these
   missing against a peer's console; they cannot be built here until the API has
   them, and pretending otherwise would put the second interface back.

#### 4b. OpenLayers for the viewer, Esri's SDK for the probe — 2026-08-16

**Owner decision, asked for twice.** §4 chose the ArcGIS Maps SDK and its argument
was sound: the most demanding Esri client pointed at our own service is the closest
available test of ArcGIS compatibility. **That argument is about testing, and it kept
being spent on the thing an operator looks at every day.**

So the two jobs are now two artefacts:

| | Library | Where | Why |
|---|---|---|---|
| **Layer viewer** | **OpenLayers 10.3.1, vendored** | `/studio/view.html` | BSD-2-Clause, 858 KB committed. No CDN at runtime, so the console's policy needs no third-party origin for it, an air-gapped deployment (Q-15) works, and no third party learns this server exists |
| **Compatibility probe** | ArcGIS Maps SDK, from Esri's CDN | `/studio/map.html` (then `/console/map.html`) | §4's argument, kept where it applies. It is the real client and it asks for everything |

Both are offered from the services directory as `View in: Map · ArcGIS SDK`, so the
distinction is visible rather than implied. GeoServer ships an OpenLayers preview for
the same reasons, which the owner pointed out.

**No server change was needed.** OpenLayers has an `EsriJSON` format, so it reads our
FeatureServer as it stands — and `f=geojson` remains accepted-and-ignored rather than
becoming a thing we had to build to satisfy a client library.

#### 4c. The basemap is OpenStreetMap's rendered tiles — owner's decision, 2026-08-16

**§4a removed the hardcoded OpenStreetMap basemap on the finding that their tile
servers had blocked us. That finding was half right and the half that was wrong
matters.**

There is no 403. Asked directly, the same tile URL answers **200 twice over**: a
6.9 KB PNG reading *"418 Access blocked — App is not following the tile usage
policy"* when the request carries no identification, and the real 40 KB rendered map
when it carries a `Referer`. What stripped the `Referer` was **our own
`Referrer-Policy: no-referrer`** — the §66 security gate's correct fix, whose cost
nobody had measured. The console now sends `strict-origin-when-cross-origin`, which
is the origin and nothing else: no path, no query, so the credential that header
exists to protect still cannot leave.

**The policy position, stated rather than buried.** OpenStreetMap's Tile Usage Policy
says their volunteer-run servers are not for use as an application's default basemap.
That constraint was put to the owner three times, across §4a and two later rounds,
and the decision to use them anyway is the owner's — recorded here as theirs, with
its cost: a deployment that leans on donated infrastructure for its default, and a
basemap that can be withdrawn by somebody else. The tile-template box remains, so a
deployment can point at a provider it is entitled to, and attribution is displayed
because the licence requires it.

**And the deeper thing this exposes.** The console depends on a third party to render
a map *because this product cannot render one*: ADR-004 is `DEFERRED` and v1-scope §3b
cut rendering entirely. That was a scope decision about serving rendered maps to
customers; nobody noticed it also decided what our own console can show. Recorded as
[Q-110](../open-questions.md).

#### 4d. OSM *data* and OSM's *tile service* are different questions

The owner drew this distinction and it is the one that unlocked everything above.

- **The tile service** has a usage policy, and §4c is where that lands.
- **The data** is ODbL and free to use. Nothing stops an operator importing it.

**The route, demonstrated end to end on 2026-08-16 rather than asserted:** Turkey's
81 provinces, 973 districts' worth of boundary lines, 46,041 major road segments,
the country polygon and 1,421 place names were fetched from Overpass, imported
through `POST /admin/hosted/import`, and served as **our own vector tiles** — 118 KB
at z6 for provinces, `X-Tile-Cache: HIT` on the second request. 44 MB of real OSM
geometry through the import and tile pipeline, which had never seen data of that size
or shape before.

**Why this shape is the right one:** we redistribute nothing, so the ODbL obligations
stay with whoever imported the data; there is no size ceiling; and it exercises the
pipeline this product is built on. It is also the honest answer to *"can we have
province and district boundaries"* — vendoring them worldwide is 21 MB from Natural
Earth at 10m and still excludes Turkey's provinces at 50m, while an import is exactly
as detailed as the operator needs.

The ring-stitching for the country polygon is worth one note: boundary relations come
back as member ways in no order and no direction, and joining them into closed rings
is an algorithm that fails quietly. It was written for **one** relation rather than 81
because one is checkable — the check being that every way joins and every ring
closes, reported rather than patched. It did: *2 closed rings, 0 ways left unjoined*.

### 5f. What the rebuild found, 2026-08-16

Four defects, and none of them was in the new screens. Recorded together because
they share a shape: **something that fails invisibly, presented to the reader as a
dead page.**

| Found | What it was | Where it went |
|---|---|---|
| The console ran no JavaScript at all, and its sign-in leaked a password into the URL | The §66 security gate's `Content-Security-Policy`, written for three pages that carry no script, applied to a fourth that is nothing but script | [D-44](../architecture-debt.md), closed. Own policy by path; script moved to a file so the policy needs no `'unsafe-inline'`; form cannot serialise a credential without script |
| Three of six layers were unaddressable | `/admin/layers` carries no service and no layer index, so every URL was guessed as `{layerName}/FeatureServer/0` | [D-45](../architecture-debt.md), **closed 2026-08-17**: the listing carries `service`, `folder`, `layerIndex` and a built `url`, and the console's directory resolver is gone. It had to be closed rather than tolerated — the resolver could not see a stopped service, so three separate screens silently lost them |
| Every basemap tile answered 403 | A hardcoded third-party basemap whose usage policy forbids the use, probably tripped by our own `Referrer-Policy` | §4a above. No basemap ships |
| One refused endpoint blanked the whole console | The boot was a single `Promise.all`, so any rejection took all four sections with it | Each section fails in its own place and says the list is not empty but refused |

**The common lesson, and it is about tests rather than about any of these bugs.**
Every existing check passed throughout. The header tests asserted the headers were
present, which they were. The multi-layer tests asserted the *service* answered at
each id, which it did. What nothing asked was whether a **client** could still use
the result — whether the page ran, and whether a layer name could be turned into a
URL. Both of those are now asserted:
`The_console_may_run_its_own_script`,
`The_sign_in_form_cannot_leak_a_credential_without_script` and
`Every_catalogued_layer_is_findable_in_the_services_directory`.

The third is the interesting one: it is an invariant **between two documents** —
the catalogue and the services directory — and neither document alone was wrong.

Also added in the same pass, and these are ordinary: per-section failure isolation,
a filter over name, table, source and owner; sortable columns as real buttons with
`aria-sort`; Map and Tiles back on the row, because comparing two layers on one map
was the common case and the drawer made it cost two openings; a **Contents** panel
reading the layer's own service document for geometry, reference, extent and
fields, which was the one question about a layer the console could not answer; and a
timestamp on the Operations figures, because a runtime number with no time on it is
read as *now* for as long as the tab stays open.

### 5b. Measured against `honua-console`, 2026-08-16

§2 rejected the separate-deployable shape and named `honua-console` as the thing
it was rejecting. That was a judgement about *shape* made without looking at the
thing. It has now been looked at — from its public repository, its README and its
own route map — and the shape judgement survives while the **scope** judgement
does not.

**What they have.** Blazor on .NET 10, MapLibre GL JS, Vega/Vega-Lite for charts,
Cesium for 3D, an optional MAUI Blazor Hybrid desktop host, Apache-2.0, pre-1.0,
published as `ghcr.io/honua-io/honua-console:nightly`. Roughly fifty routes across
four surfaces — Studio (authoring), Catalog (discovery), Operate (administration),
Share (public and embed) — with code splitting so the public surfaces paint without
loading the administrative ones.

**What we have.** One 28 KB static HTML page and a 4.5 KB map page, served from the
same process (then `/console`), with three sections: Services, Data sources, New hosted
layer. That is not the same category of artefact and pretending otherwise would be
useless.

**Route by route, after the rebuild:** the surface-by-surface mapping is in
[peer-capability-comparison.md](../research/peer-capability-comparison.md) §3a —
Operate largely overlaps and we hold one screen they do not (route governance),
Catalog partially overlaps and differs structurally because they have an item model
and we have layers, and Studio and Share are theirs entirely. It also names the three
capabilities our own admin API has that our console does not reach.

**Three things keep this from being a straight loss, and they are all true:**

- **Their flagship surface is shelved by default.** Studio's own README lists
  authoring for *"queries, analyses, maps, dashboards, reports, forms, apps, and
  workflows"*; their route map records that `/studio/query`, `/studio/analysis`,
  `/studio/map`, `/studio/dashboard`, `/studio/app` and `/studio/proof` all
  *"render unsupported state by default"* behind a `studio-builders` capability
  flag. Forms, reports and workflows are live. Five of eight builders in the
  headline are not on.
- **Much of the rest is licence-gated in the UI itself.** `edition:Pro`,
  `edition:Enterprise`, `entitlement:identity.oidc`, `entitlement:alerts.dwell`,
  `entitlement:channels.slack` — insufficient tiers *"render as upgrade tiles"*.
  A Community operator meets a console partly composed of locked doors. Ours is
  copyleft and free, and that is a product difference rather than a UI one.
- **The shape argument in §2 stands unchanged.** A second deployable in version
  lockstep is the right answer for several servers under one UI and the wrong one
  for *one deployable against one PostgreSQL*.

**But the scope argument does not stand, and this is the finding.** Our console
being three sections is not a consequence of choosing one deployable. Static files
and `fetch` from the same process can host far more than three sections. §7's
*deliberately not in this version* is doing work it was not designed for: the
audit screen is already owed there, and this comparison adds that we have nothing
for **jobs**, nothing for **observability**, and no **public or embeddable**
surface at all.

### 5c. Four rules taken from their console, and why each transfers

Recorded per [ADR-030](ADR-030-reading-the-reference-implementation.md) condition 1
as derived from reading the reference. None is adopted here; each is a rule this
ADR should answer for.

1. **Frozen URLs.** They require certain routes — public embeds and open-data item
   pages — to answer 200 at both their legacy and current paths, forever, because
   external pages embed them and catalogues reference them. **This transfers with
   more force than it has for them:** every `/rest/services/...` URL is written
   into ArcGIS client configuration, and Q-16's migration story is that those
   clients keep working. A URL an ArcGIS client holds is not ours to reorganise.
   ADR-023 governs the directory's shape and says nothing about its permanence.
2. **Missing-binding states.** A server-bound page with no server configured
   renders an explicit *missing-binding* surface rather than mock or empty data.
   The rule behind it is the same one this project applies to tests: a screen that
   looks like it worked with nothing behind it is worse than one that refuses.
3. **One canonical surface per exception category** — unauthenticated, forbidden,
   missing, unsupported, unavailable — *"prevents bespoke error copy"*. We already
   hold the server half of this in `ErrorResponse`; the console has no equivalent
   and would grow one message at a time without the rule.
4. **Split so the public surface does not carry the private one.** Their share and
   embed bundles load without Studio or Operate. If our console ever serves an
   anonymous page, the same separation is a security property and not only a
   performance one — the administrative code should not be shipped to a caller who
   may not use it.

### 5d. The console's licence is known; the server's is not

`honua-console` is **Apache-2.0**, stated in its own repository. That is a real
data point and it settles nothing about the server, whose licence remains
[Q-106](../open-questions.md) — the local checkout's `LICENSE` file is scrubbed and
the owner has said it is not the real one. Recorded here because it is evidence
about the project's licensing posture and because a partial answer looks like a
full one if nobody writes down which part it is.

### 5g. The Anonymous view, added 2026-08-16

**Every request the console makes carries the administrator's own token, which
made the console the one viewer guaranteed to see more than any real caller.**
That is a strange gap in a product whose promise is that an unmodified ArcGIS
client keeps working (Q-07, Q-17): the screen an operator trusts on the first day
was structurally unable to answer the first day's question.

The screen asks it by asking. For every catalogued layer it makes the three
requests a client makes — the service document, the layer document, and a count
query — **with the `Authorization` header left off**, and compares what came back
against the sharing scope the catalogue says was intended. Two disagreements are
marked and nothing else is: something shared `private` or `organization` that
answered anyway, and something shared `public` that did not.

**The layer-to-service mapping is resolved with the session and the probe is made
without it**, which is the whole design in one line — you cannot ask what a
stranger sees at a URL you were unable to find.

**It also closes a trap this project fell into.** [ADR-018](ADR-018-authorization-and-roles.md)
answers *absent* and *forbidden* identically, which is right, because the
alternative leaks the catalogue to anyone who can count 404s. The cost is that a
404 tells an administrator nothing, and [D-45](../architecture-debt.md) is that
mistake made against our own server by the person who wrote the refusal. This
screen is where the two are told apart, using the one thing a 404 cannot carry:
what the catalogue says was intended.

**One half of it is now a test rather than a screen.**
`AnonymousAccessConformanceTests` pins the direction that needs no fixture — the
services directory must not advertise what an anonymous caller cannot then read —
in two cases, and the suite gained an `AnonymousAsync` on its client whose only
distinguishing feature is the header it does not send. The other direction, that
something *absent* from the anonymous directory is also refused, needs a private
service to exist; in an all-public deployment such a test would pass while
proving nothing, so it is left to this screen and said so in the test file rather
than implied by a green suite.

**Not verified visually.** The functional core was measured directly — the same
twenty-seven requests were made by hand against a live server and every one
answered 200, which is correct because the anonymous directory listed all nine
services and the directory filters by sharing. The layout was not seen: driving
the page needs a sign-in and a click, and the screenshot route available here
takes one shot of one URL. Recorded because *"the tests pass"* and *"somebody has
looked at it"* are different claims, and this session has twice found a defect
that only the second one catches.

### 5h. The create surface, added 2026-08-16

The drawer held two ways to make a **hosted** layer — design a schema, import a
file — and nothing for the three things an administrator of an existing estate
actually does first. It now holds four, and is titled *Create* rather than *New
hosted layer*, because a heading that names one of four things is worse than a
general one.

**Publish a registered table.** Pick a connection, and the tables are read from
the database at that moment rather than remembered from registration. The
publish request needs six things the operator should not retype from a table they
just chose, and all six come from the probe — except `identityColumn`, which is
asked for explicitly, left **empty**, and offered as a one-click nomination
beside the probe's object-id column. That is not a UI preference: Q-57 makes
identity for a registered table *"declared, not inferred"*, and prefilling would
present the server's guess as the operator's decision about which column an edit
lands on. [D-50](../architecture-debt.md) records that the probe cannot yet offer
better candidates.

~~**An empty service, and a group inside one.** In that order, because it is the
order the structure needs: the service exists before its layers, groups exist
before the layers nested under them, and the publish form's *into service* field
is what puts a layer in one.~~ **Superseded 2026-09-06 —
[ADR-057](ADR-057-composing-and-publishing-a-service.md) §5h and its condition 4, by
owner decision: *hayır, katmansız servis yaratılamaz*.** The drawer keeps only its group
form, `POST /admin/featureservices` answers 400 naming `POST /admin/publish`, and the
order this paragraph describes — container, then groups, then layers naming a group by a
numeric index — is the sequence a design review called *the API rendered as a form*. A
composition is written in one request now, and **the order the structure needs is the
server's problem rather than the operator's**, which is the part of this paragraph that
was wrong rather than merely out of date.

**Empty services have not stopped existing**, and nothing below about finding and
removing them changes: unpublishing the last layer still leaves the container, which is
what the paragraph after next is about.

**Building it found the defect that made it necessary.**
[D-47](../architecture-debt.md): the publish endpoint accepted `serviceName`,
`parentLayerId` and `cacheSeconds` and silently discarded all three, because the
record's optional parameters let a ten-argument call compile. So the owner's
2026-08-15 correction — *"a service is a combination of layers"* — was
implemented in the catalogue, tested at that level, and unreachable through the
API, while `POST /admin/featureservices` answered with a note telling the operator
to use the parameter that did nothing. Fixed and verified end to end. **The reason
it survived is a gap this ADR should state plainly:** there is no in-process host
harness in `tests/`, so no test exercises an admin endpoint's mapping from request
to domain record. Every layer below is covered.

**What the surface deliberately did not claim, and no longer has to.** This
paragraph used to say a service and a group can be created here and not removed —
[D-48](../architecture-debt.md) — and that the copy would not offer a delete it could
not perform. **Both deletes exist as of 2026-08-17**, and building them found the half
D-48 had not stated: an empty service appeared in *no listing anywhere*. The layer table
lists layers; §5e's *System services* is a different table holding the geometry service.
So the residue of publishing and unpublishing was invisible, and the missing delete had
nothing to be missing from. §5j has the shape.

One claim is still withheld, and it is now [D-54](../architecture-debt.md): unpublishing
the last layer leaves the service behind. A publish-created service and a
deliberately-created one are the same row, so removing it automatically would guess.

### 5i. The layer's settings became a page, 2026-08-17

**The owner's correction, in their words:** *"ayrı bir sayfa yapalım. gerçekten şık
değil. iç içe ve karışık"* — make it a separate page, it is not elegant, it is nested
and confused — and then *"single page olsun. ileri geri yapalım"*: one page, with back
and forward.

What was wrong was the structure and not the styling. Per-service settings had grown
from three controls to six pages' worth — faces, feature capabilities, six cost
ceilings, cache lifetime, style, sharing, endpoints — and all of it was going into a
620-pixel slide-over that overlaid the table it was about. A settings screen inside a
drawer inside a console is nested twice. Three visual concepts were built before this
and all three were rejected; what they got wrong was the same thing each time, which is
why the fourth attempt took its structure from the owner's own reference — ArcGIS Server
Manager — rather than from taste:

- a left column of short settings pages instead of one long scroll,
- a breadcrumb naming what is being edited, so it is clear what Save will change,
- one Save and one Cancel for the session rather than a button beside every control,
- capabilities as names in a grid, with no prose between them,
- numbers written as sentences with the unit outside the box.

Their pages are not copied onto ours: we have no instance pool, so *Pooling* becomes
*Limits*, which is where ADR-031's ceilings live. The drawer keeps exactly one job,
**Create** — a short form you fill and dismiss belongs over the page; a service's
settings are a place you go.

**The hash is the route, and that is a constraint rather than a preference.** Back and
forward have to work, which they could not when the editor was a slide-over: the browser
had no record that anything had opened, so Back left the console. Every surface is now an
address — `#/services`, `#/layer/tr_ilce/limits` — reached by an ordinary link, so the
browser's own buttons, middle-click and copy-link cost this console no code. A pushState
path was rejected because the console is static files: `/server/layer/tr_ilce` is a
route nothing here answers, and an address that 404s on refresh is not an address.
Verified by driving it — services → limits → caching → Back → Back → Forward lands on
`#/layer/tr_ilce/limits` with that page shown and its left column marked.

**Reconsidered and declined by the owner, 2026-08-18**, when they asked *"# yerine başka bir isim
kullanamaz mıyız?"* — and then *"tamam kalsın # o kadar önemliyse"* once the price was on the table.
Recorded because the paragraph above rejects `pushState` on a premise that had stopped holding: the
console being static files is a reason only while nothing answers those paths, and two catch-all routes
returning `index.html` would answer them. So this is a live option, not a closed one, and what follows
is what it costs rather than why it is impossible.

- **The character cannot be renamed.** `#` is the fragment delimiter in RFC 3986. Removing the fragment
  is the only version of the request that exists.
- **Measured surface: about 47 touch points** — 19 `href="#/…"`, 9 writes to `location.hash`, 12 reads
  of it, 7 `surfaceHref` calls — plus the router, plus a click interceptor, plus the server routes.
- **Nothing functional is gained.** Back, forward, middle-click and copy-link already work; they are
  what the hash buys, and it buys them with no code.
- **The risk is the interceptor.** Without one, every navigation becomes a full page load. With one,
  `ctrl`/`cmd`/middle-click and `target="_blank"` must fall through or *open in new tab* silently
  breaks — which is this console's most-repeated defect shape: a control that looks right and is not.
- **And the ten-line server route was not ten lines.** The first attempt was written and measured
  before the owner decided, and it answered `/studio/console.js` with `text/html` — the catch-all won
  over the static file middleware for that path, so the console's own script was being served as a
  page. Cause not chased, because the change was dropped; the symptom is the point. **A change
  described as small had already broken the console once**, and that is the strongest argument in this
  note for leaving the hash alone.

Two behaviours exist because of what the drawer used to hide:

- **A refresh does not eat an unsaved figure.** Start, Stop, Map and Tiles all re-read
  `/admin/layers` and the General page shows the state they change, so it is redrawn —
  carrying whatever is typed on the other pages across the redraw. Moving between pages
  does not re-read at all: all six are in the document and the move is a class.
- **Asking to draw something takes you to where it draws.** The map belongs to the
  Services surface, so Show and Tiles pressed from a layer's own page used to add a layer
  to a map on another screen and leave the operator looking at an unchanged one — which
  is indistinguishable from a dead button, and this console has spent three rounds on
  that class of fault ([D-46](../architecture-debt.md)).

**Extracting the stylesheet is what broke it, and the way it broke is the lesson.** The
page's `<style>` block moved into `console.css` so the two surfaces could not drift, and
the console's Content-Security-Policy permitted inline style but not a stylesheet file.
Every rule was served and none applied. It did not look like a refused request: without
`.view { display: none }` all five screens stacked into one document, which reads as a
layout bug. D-46 instance (7), and the fix is a test that enumerates whatever a console
page references rather than naming the kinds somebody thought of.

### 5j. Services became visible, and removable, 2026-08-17

The console could create a service and a group and could not see or remove either. The
missing listing was the worse half: **a service is created implicitly by publishing**, so
the first thing an operator does creates one, and unpublishing the last layer leaves it —
advertised in the services directory as a FeatureServer with no layers, and reachable
only through SQL against the platform store.

- **A Services panel**, from `GET /admin/featureservices`: every service with its status,
  sharing, owner, and **what it holds**. The counts are the screen's whole reason: the
  question asked of this list is *which of these can go*.
- **Delete is offered only where it would work.** A service holding anything shows the
  button disabled, titled with what is in the way — *It holds 3 layers, 1 group.* §5h's
  rule was *do not offer a capability the API lacks*; this is its twin, *do not offer one
  the API will refuse*, and it turns a 409 the operator would have to trip over into a
  sentence they can read first.
- **A group is unmade where it is made.** The Create drawer's group form now lists the
  chosen service's groups with a delete each, disabled while a group has children. There
  is no other screen a group could belong to: it holds no data and has no settings of its
  own. The list is read from the **service document** — the same one the map reads, where
  a group is a layer of type `Group Layer` with its children in `subLayerIds` — rather
  than from a second admin route that could disagree with it.

**Nothing is cascaded, and the refusals say why.** Deleting a service does not unpublish
its layers, because unpublishing purges tiles and forgets a remembered shape and should
be asked for per layer, where the response says the source table was not touched.
Deleting a group does not reparent its children, because that would move them in every
saved web map that points at them.

## 5f. The console is redesigned to an owner-supplied target — 2026-08-17

The owner supplied two screenshots and a written brief: the working Server screen, and a target
visual language. *"Rebuild the Server frontend so that it closely follows the visual language of
Screenshot 2 while preserving the functionality of the existing application. This is not just a
color/theme change."*

**What it is: a navy navigation column, a bright workspace, and one token layer under both.** The
stylesheet is ordered tokens → shell → components, which is the part that matters more than the
colours. The sheet it replaced was a flat list that had grown a section at a time — four places
setting a panel's padding, two palettes' worth of greens — and **both of that day's UI defects were
shapes rebuilt because finding the existing one was harder than writing another** (D-46 instances 8
and 10). Tokens plus a short component list is the cheapest defence against the next one.

**Nothing behind the frontend moved**, and that was the brief's own first constraint. Same routes,
same calls, same folder behaviour, same start/stop, same privileges, same Server/Studio gate. The
router changed in one respect: it fills two slots instead of one, because navigation moved into a
column and an action button is not a navigation item.

**Where the implementation departs from the reference, each time for the same reason.** The brief
said *"Do not fabricate backend data"*, and three of the reference's components would have required
it:

- **Server resources shows figures, not percentages, and no sparklines.** `/admin/health` reports
  process CPU time, managed heap bytes, tile-cache size, uptime and core count. CPU *is* a real
  ratio — process time over wall-clock times cores — and is drawn as one. Heap and tiles have no
  quota to be a share of, so they are the numbers they are. A sparkline claims a history this server
  does not keep.
- **Service health has two states, not three.** `service.status` is `started` or `stopped` by a check
  constraint. An *Error* row would name a state this server cannot be in; the reference has one
  because its services can fail to start and ours cannot. The widget also hides itself in a folder
  with no feature services rather than showing zeroes.
- **No grid toggle, no notification bell, no avatar menu.** There is no grid view, there are no
  notifications and there are no per-user settings. Refresh and Collapse are in because both are
  real: one re-reads the two listings, the other sets a class and remembers it.

**And one place where the reference improved the information design rather than the appearance.**
Its rows carry a single verb with everything else behind an overflow. Ours had Stop and Delete side
by side, so the destructive action had the same weight as the routine one; Delete moved into the
menu, where its refusal — *it holds 3 layers, unpublish them first* — has room to be a sentence
instead of a tooltip. Badges are now only for the two things that are states, and each carries a dot
so a state is never colour alone.

**Both surfaces share the shell**, which the brief asked for in as many words: *"The Server and
Studio interfaces should ultimately feel like two parts of the same Graticula product."* The switch
tells them apart by hue — teal for Server, violet for Studio — so *which environment am I in* does
not depend on noticing which button is darker.

### 5g. The refinement pass — 2026-08-17

The owner's verdict on §5f: *"functionally good, but visually it is still too conservative and too
close to the old Graticula UI… it still feels like old enterprise admin UI with a modern sidebar."*
They were right, and the diagnosis is more useful than the fix: **§5f copied the reference's
structure and left its personality behind.** A sidebar, a page heading and a token layer are the
skeleton; what makes the reference read as a product is where the colour is and how much depth each
surface has, and §5f had one teal doing every job on two very different backgrounds.

**The one substantive discovery: two accents, not one.** A single teal is why the page went
monochrome. On navy it reads as grey-green, so the selected section had no presence at all; bright
enough to hold a dark surface, it would be garish on white. So `--cyan` is the sidebar's accent and
`--accent` (teal) is the workspace's, and the primary action is the one place they meet — a
cyan→blue→violet gradient, and **the only gradient in the workspace**, because one element having it
is what makes it read as *the* action rather than as a theme.

**The sidebar is four painted layers and no asset:** a base navy so contrast is decided by one
colour, an indigo wash from the top-left where the eye enters, a cyan glow low down, and the
graticule grid masked to fade in downwards. The decorative element the brief asked to restore is the
product's own subject — a GIS layer stack, three skewed rectangles in CSS. No stop is above 22%
opacity; *keep it professional, do not make it neon* is a ceiling on numbers rather than an
intention.

**Service health became a ring**, which is a `conic-gradient` with a hole punched in it: the arc
lengths are the real counts, no chart library and no second copy of the numbers.

**Server resources got the sparklines, and this is where the brief's two instructions had to be
reconciled.** It asked for the reference's sparklines *and* said not to fabricate — and both are
possible, because a line does not have to come from the server's history. It comes from **ours**:
the page samples `/admin/health` every five seconds while the widget is on screen and draws what it
has actually observed. The first sample draws nothing and says *sampling…*; after a minute there is a
minute of real measurement. It is thrown away on reload, which is the honest bargain — the
alternative is a server-side time series, and that is a decision about storage and retention rather
than a chart. **A flat series gets a flat line**, deliberately: it means nothing changed, and
inventing a wiggle for it would be the exact thing being refused. The metrics are unchanged — CPU as
a real ratio, heap and tiles as figures, uptime — because the brief said to redesign their
presentation rather than invent new data.

**Error is now the third row in Service health, at zero and dimmed, with the reason on hover.** The
owner asked for Started/Stopped/Error twice, in the reference and in the brief. `service.status` is
`started` or `stopped` by a check constraint, so this server has no error state — showing the row
undimmed would imply a detection it does not do, and dropping it would ignore a direct instruction
given twice. Zero, dimmed, and a title that says why is the only reading of both that is true.

**In the table, three of the brief's items turned out to be one.** Row height, vertical alignment and
subtle separators: at twelve pixels of padding a full-weight rule is needed to tell rows apart, and
at sixteen the spacing does it, so the rule drops to a tint. The preview lost its grey box — it is
white like the row, held by a hairline — because *"the GIS preview should feel integrated into the
row rather than placed inside a generic gray rectangle."* And the name/caption pair differs in four
ways at once (15.5 against 12.5, 650 against 400, ink against muted, counts in the monospace face),
which is what *"do not just change font sizes"* asks for.

**What is still not reproduced, and each is the same reason as in §5f:** no grid toggle, no
notification bell, no avatar menu. There is no grid view, there are no notifications and there are no
per-user settings, and a control that implies one is worse than a control that is missing.

### 5h. The polish pass, and the one place two instructions disagreed — 2026-08-17

Six items, all visual, all in the stylesheet. Five were arithmetic: the type scale up about a tenth
**in the tokens rather than per component**, so the ratios between the levels — which are the
hierarchy — moved together; the sidebar illustration up to 0.78 opacity with a survey-dot field and a
cyan-violet glow behind it; two washes under 4% giving the workspace an ambient tint while the panels
stay white; the resource values to 15 pixels of monospace with 28-pixel charts; and the primary action
to 21 pixels of horizontal padding.

**The sixth was the useful one.** *"The long explanatory paragraph makes the widget feel like a debug
panel."* True, and the text still earns its place — CPU is a ratio since start and the lines are the
page's own samples, so a first-time reader needs both facts or the numbers are misread. So it is one
glyph with a `title`, complete rather than shortened to fit: a caveat trimmed to look tidy is a
caveat that stops being true.

**And the place the brief contradicted itself, which is worth recording because the resolution was a
judgement rather than a calculation.** It asked for a 10% scale-up including row heights, *and* for the
populated table to be compared against the approved screenshot. Those pull opposite ways: the
reference's own rows are **tighter** than a proportional increase produces — about 82×50 of preview in
a 77-pixel row, against the 122×84 in 121 pixels that +10% gave. Both cannot be satisfied. **The type
kept the increase and the picture did not**: 104×70 in a ~101-pixel row, so names, labels and the
folder panel are a tenth larger as asked while the row is much nearer the reference's density. Said
out loud here because the alternative is silently picking one and calling it done.

### 5i. Five details the owner named, and one left undecided — 2026-08-17

Item-by-item feedback on the polish pass, each of which turned out to be about a *distinction* rather
than about appearance.

**The sidebar's base is now the owner's own number.** *"Soldaki menünün renkleri daha şık. oradaki
ana renk `#171b49`, bizdeki `#111d34`."* The two are within a few points for brightness — the
difference is hue: ours sat at 210° (a blue-grey slate) and theirs at 234° (an indigo). Slate reads as
*chrome*; indigo reads as a product that decided on a colour. **The derived tokens moved with it
rather than being kept**, because a line and a hover surface mixed from slate against an indigo base
is exactly what makes a palette look almost-right.

**The layer stack holds a map.** *"Katmanlardaki şu geçişler daha güzel. içinde de harita var gibi.
bizde içi boş dikdörtgenler var."* Both halves fair, and the second is the one that matters: a hollow
outline says *box*; a filled surface with a graticule and a scatter of nodes says *layer*. Four plates
now, each with a gradient fill, a grid drawn on it, nodes, and a rim light — rotated once as a group,
so the grid inside each plate is skewed with it and reads as a map in perspective. Teal on top and
violet at the bottom, which is the reference's order and not arbitrary: down the stack is further
away, and a violet top plate makes the stack read as sinking rather than as layers seen from above.

**Sharing carries a glyph.** *"Sharing'deki icon mantığı güzel."* It is: a globe and a padlock are
read before a word is. Inline SVG rather than an icon font or emoji — emoji are colour glyphs that
ignore `currentColor`, which is fatal for a set whose job is to inherit the hue that already
distinguishes the three scopes. **The glyph replaces the dot rather than joining it**, because a badge
carrying both is two signals for one fact.

**Two greens, for the reason there are two teals.** *"Service health'taki donut'ın renkleri, alttaki
lejant renkleri güzel."* Ours were dark because one token was doing two jobs: text on a pale badge,
where it must be dark enough to read, and an arc or a dot, where dark reads as muddy. `--ok` stays the
text colour and `--ok-dot` is the indicator. **The ring still shows no amber or red at nine-of-nine
started**, although the reference does with the same data — those arcs are decoration, and the whole
point of an arc is that its length is the count.

**The verb carries its shape**, and the folder rail distinguishes the root from a folder — a house
against a folder, which is a real distinction: the root is where a service lives when nobody put it
anywhere, and giving it the same icon as `turkiye` would say it is one folder among four.

**Left alone: whether Server and Studio should look like two buttons.** *"Server ve studio ayrı
butonlar gibi, hangisi daha güzel bilemedim."* Unchanged, deliberately — the current form is a
segmented control where the filled half is where you are and the hue says which environment that is
(teal for Server, violet for Studio). Two independent buttons would read as two actions, and one of
them is not an action, it is where you already are. Recorded as undecided rather than quietly settled.

### 5k. The console is tested in a browser — 2026-08-18

Four console defects in two days, every one found by the owner pressing a button, every one a click
deep. §2 says this console is *a client with no back door*, and that is exactly why nothing could
test it: there was no assertion about these pages except that the files they ask for are served.
D-59 named it; `tests/Graticula.Console.Tests` closes it, and this section records the decisions
inside that, because each of them could reasonably have gone the other way.

**A real browser, not a DOM shim.** Two of the four defects are invisible without one. `[hidden]`
losing to an author `display` is a cascade fact — a shim that reports the attribute reports
*hidden*, which is precisely the wrong answer that shipped. And the single-layer shortcut broke only
*which route a click chose*: every part worked in isolation, so a test that navigated by setting
`location.hash` would have passed on all nine services while a person could reach one.

**No automation package.** Chrome speaks the DevTools protocol over a WebSocket;
`ClientWebSocket` is in the framework; launch, navigate and evaluate is the whole surface this suite
uses. That is three hundred lines, most of it launching a browser and finding its debugging port
honestly — counted rather than estimated, because the first draft of this paragraph said ninety.
Playwright and Selenium each add a browser download to a workflow whose runner already carries
Chrome, in exchange for auto-waiting and a selector engine nothing here needs — §6's *what concrete problem does this solve* answers itself. **The trade is written where it
would be revisited**, in the project file: this client has no waiting primitives, no selector engine
and no second browser, and if the suite grows to want any of them, adopting a package is the right
answer and that note is the record of when it stopped being wrong.

**Reads reach the server; writes never leave the page.** The subject of these tests is which code
path a click takes, not what the server does next — and pressing **Stop** for real would stop the
operator's service to prove that a button works. So `fetch` is replaced for anything that is not a
`GET`, and the recorded request *is* the assertion: a click that reached the button shows up in the
recording, a click that fell through to the row shows up in the address. The other half — that the
server does what the button asked — is the conformance suite's, on the other side of the same wire.

**Every test comes in a pair, as a rule.** The cheapest way to pass *Stop must not open the
service* is to stop the row navigating at all; the cheapest way to pass *a cookie session is not
painted as an administrator* is to paint nobody. So the opposite direction is asserted beside each
one. Three of the seven tests exist only for that, and they are not padding: each is a defect that
a narrow fix to its partner would introduce.

**And each test was falsified before it was believed.** The dispatcher's exception list, the boot
gating on `authenticated` rather than on a token, `[hidden]` left to lose, and Limits removed from
`SERVICE_PAGES` were put back one at a time into a running server's `wwwroot`; each time exactly the
test that should go red went red, with the message it was written to give. A test that has never
failed is a claim about the future, and §3's standing challenge applies to a test as much as to a
performance argument.

**What it does not reach**, stated because a green tick is read as *everything*: Chrome only; no
check that the server honoured the click; and it needs a running server, so in CI it runs in the
conformance job rather than the unit one. The publisher case removes `admin:manageServer` from the
server's own `/rest/whoami` answer rather than signing in as a member who lacks it, because there is
no `DELETE /admin/members` and a suite that created one would leave a live publishable account
behind on every run — [Q-116](../open-questions.md). That is why
[ADR-034](ADR-034-server-and-studio.md) condition 1 stays partly discharged rather than closed here.

### 5l. Symbology becomes a screen — 2026-08-18

ADR-033 built the canonical document and both derived faces; this is the page an operator
uses it from, and it is in **Studio** because the endpoint behind it asks for
`content:publishFeatures` — choosing what a layer looks like is the job of whoever published
it, which is §5c's rule applied to appearance.

**It is not D-61 returning.** That defect was a *service* fact edited on each of its layers'
pages. Symbology is the opposite: the storage is a column on `layer` and the page is the
layer's. The service style, which orders and filters across layers, stays on the service
(ADR-033 §5d) and still wins for the tile face when one is stored.

**Three things on one screen, and the third is the reason for the page.** The canonical
document in an editor; what an ArcGIS client actually receives, read-only, because it is a
projection and not a second place to edit; and **what the projection could not carry**. The
losses are a block under the editor rather than a line in a toast, because a toast is read
once and dismissed while a conversion that lost four things needs to still be saying so when
the operator comes back. Amber, not red: none of it is broken, and a page that cried wolf
about a dashed line would train its reader past the one that matters.

**It reads itself, unlike the service style beside it.** That page has a *Fetch current*
button because a style can be a megabyte and is usually absent. A layer's symbology is one
symbol and the whole value of the screen is knowing what is there now — so the state line
promises *Reading…* and then says either **Generated** in words or how many bytes are
stored. §5b makes a generated appearance a real answer; an empty editor and a failed load
look identical, which is the small lie D-46 keeps catching.

**Two browser tests hold it** (`SymbologyPageTests`): the page shows what a client receives
and says which of the two states it is in, and a returned loss list appears under the
editor. The second traps the write in the page, because what is under test there is the
rendering — the server's half is `SymbologyConversionTests` and the measurement in ADR-033
§5i.

## 6. Consequences

- **A migration**, taking the platform schema to 6. Expand: one column with a
  default, so `minimum_reader_version` does not move.
- **Every read path gains a status check.** The catalogue filters, the metadata
  documents and the query endpoint refuse with 503, and `applyEdits` refuses
  too — editing a stopped service is exactly what stopping it is meant to
  prevent.
- **The console is a static-file surface**, which means the server now serves
  HTML. That is a new attack surface and it is why §8 condition 2 exists.
- **ADR-017 §7's deferral is discharged** for the console question and stands
  for the other two.

### 5a. The layer designer, added 2026-08-14

Owner request: *"you can design the layer in portal/server screen to create a
feature class in db."*

Two forms side by side, because they are the same act with different starting
material — **hosted means the datastore holds the feature class**, not that a
file produced it. Designing a schema is for data somebody is going to collect;
importing is for data they already have.

**It obeys §2's constraint exactly.** Both forms post to admin endpoints that
existed before the screen did and that `curl` can drive identically. The screen
adds no capability; it adds a place to use one. The field-type list, the reserved
names and the geometry types are all the server's answers, and when the server
refuses — a name in use, `objectid` as a field, a type it does not know — the
form shows the server's sentence rather than "request failed".

**What it reports back is the server's answer, not "done".** The import response
carries the row count, the inferred field types and the reprojection note, and
all three are things somebody uploading a file wants to check before trusting
it. A screen that says *success* throws that away.

---

**State.** *Catalogue*: a service's **started/stopped status**, a column on the service row
(§3) — so an operator's stop survives a restart and is the same on every node, which is the
whole reason it is not a runtime flag. *Runtime*: none of its own. The console holds a token in
the browser's `sessionStorage` and nothing on the server; every screen it draws is a document
the admin API already answers.

## 7. Deliberately not in this version

- **An audit viewer.** The rows exist and the endpoint does not. It is the
  screen most likely to be wanted next and it needs paging, which needs the
  query surface ADR-008 has not built.
- **Editing features from the console.** `applyEdits` exists; a drawing tool is
  a different discipline and a large one.
- **Registering a data source from the console.** It is the one operation that
  carries a credential, and a form that posts a password deserves more thought
  than a first console version can give it.

## 8. Conditions

1. ~~**The console uses no capability the API does not expose**~~ **DISCHARGED 2026-08-15.** Every screen posts to an admin endpoint that existed before it and that `curl` drives identically — including the layer designer added the same week, which was built API-first and exercised through those endpoints before the form existed. Original:, checked by the
   fact that every action it takes is reproducible with `curl`. If a screen ever
   needs an endpoint that exists only for it, that is a design failure to be
   fixed in the API.
2. ~~**The static-file surface serves nothing outside its own directory**~~ **DISCHARGED 2026-08-15.** Traversal refused three ways — `..`, percent-encoded `%2e%2e`, and a back-slash variant — each 404 rather than a file. Original:, and
   that is tested rather than assumed. Path traversal is on
   [security.md](../security.md)'s unwritten list and this is the first code
   that could suffer from it.
3. ~~**A stopped service is refused everywhere**~~ **DISCHARGED 2026-08-15, and the check grew with the surface.** The condition named three endpoints; there are now seven, and all seven answer 503: the service and layer documents, `query`, the VectorTileServer document and its tiles, `attachments`, and `queryRelatedRecords`. Each new surface inherited the check because they share one resolver — which is the argument for having one. Original:, not merely hidden from the
   catalogue — query, metadata and `applyEdits` each tested.

## 9. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-055 | A single-page console with no build step stays maintainable | `UNVALIDATED`. No bundler, no framework, no npm — which keeps the supply chain small and the file large. The trade reverses at some size |
| A-056 | Operators want start/stop rather than only sharing | `UNVALIDATED` — it is the owner's request and it matches ArcGIS Server, which is evidence about the model rather than about need |

## 10. Dissent

**Against shipping a console at all.** ADR-017 §7's instinct was sound: an
API-first product with a UI tends to grow features in the UI first, and the API
becomes the thing that lags. The mitigation is §2's constraint and §8's first
condition, and both depend on somebody continuing to care.

**Against start/stop.** It is a second availability lever beside sharing, and
two levers that both make a service unreachable is a state space where operators
guess. The counter is that they answer different questions, and that an operator
with only sharing will reach for `private` to take a service down — which is
worse, because it destroys the sharing setting they will need to restore.
