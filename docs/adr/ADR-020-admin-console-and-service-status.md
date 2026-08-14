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

1. **The console uses no capability the API does not expose**, checked by the
   fact that every action it takes is reproducible with `curl`. If a screen ever
   needs an endpoint that exists only for it, that is a design failure to be
   fixed in the API.
2. **The static-file surface serves nothing outside its own directory**, and
   that is tested rather than assumed. Path traversal is on
   [security.md](../security.md)'s unwritten list and this is the first code
   that could suffer from it.
3. **A stopped service is refused everywhere**, not merely hidden from the
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
