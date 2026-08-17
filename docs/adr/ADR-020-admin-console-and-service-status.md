# ADR-020 — An admin console, and what "stop a service" means

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM-HIGH` for the console · `MEDIUM` for the status model |
| **Decided** | 2026-08-14 |
| **Answers** | [ADR-017](ADR-017-admin-api.md) §7's deferred *whether there is a web console* · owner request for start/stop |

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

**It ships with the server, served from the same process at `/console`.**

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
matters twice over: this product is Apache-2.0 and must be redistributable by
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
| **Layer viewer** | **OpenLayers 10.3.1, vendored** | `/console/view.html` | BSD-2-Clause, 858 KB committed. No CDN at runtime, so the console's policy needs no third-party origin for it, an air-gapped deployment (Q-15) works, and no third party learns this server exists |
| **Compatibility probe** | ArcGIS Maps SDK, from Esri's CDN | `/console/map.html` | §4's argument, kept where it applies. It is the real client and it asks for everything |

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
| Three of six layers were unaddressable | `/admin/layers` carries no service and no layer index, so every URL was guessed as `{layerName}/FeatureServer/0` | [D-45](../architecture-debt.md), open in the API, resolved from the directory in the console |
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
same process at `/console`, with three sections: Services, Data sources, New hosted
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

**An empty service, and a group inside one.** In that order, because it is the
order the structure needs: the service exists before its layers, groups exist
before the layers nested under them, and the publish form's *into service* field
is what puts a layer in one.

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

**What the surface deliberately does not claim.** A service and a group can be
created here and cannot be removed — there is no delete for either on the admin
API ([D-48](../architecture-debt.md)), and unpublishing a layer leaves the service
it created behind. The copy does not offer a delete it cannot perform, which is
§2's rule about the console adding no capability the API lacks, applied to an
absence rather than a feature.

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
path was rejected because the console is static files: `/console/layer/tr_ilce` is a
route nothing here answers, and an address that 404s on refresh is not an address.
Verified by driving it — services → limits → caching → Back → Back → Forward lands on
`#/layer/tr_ilce/limits` with that page shown and its left column marked.

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
