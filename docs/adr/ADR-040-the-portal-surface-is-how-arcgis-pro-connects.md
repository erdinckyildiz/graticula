# ADR-040 — The portal surface is how ArcGIS Pro connects

**Status:** ACCEPTED WITH CONDITIONS
**Confidence:** HIGH — the road was chosen on evidence and then measured: ArcGIS Pro connects,
browses and adds a layer (§6 condition 1)
**Date:** 2026-08-20

---

## 1. Context

**Owner decision, 2026-08-20:** *"Ben bu ürünü pro kullanıcıları da kullanabilsin istiyorum yani
bunu da yapmam lazım."* ArcGIS Pro users must be able to use this product.

**They already can, and that is the part worth stating first.** A Pro user who pastes
`/rest/services/hosted/tr_il/FeatureServer/0` into *Add Data → Data From Path* gets the whole
surface: measured on 2026-08-19, Pro drove layer and service documents and then paged queries
carrying `outFields=*`, an `outSR` given as a spatial-reference object rather than a code, a polygon
`geometry` filter with `esriSpatialRelIntersects`, `resultOffset`, `resultRecordCount` and
`orderByFields`. Every request answered 200 and the layer drew.

**What does not work is adding the *server*.** Pro's *New ArcGIS Server* connection never touches
`/rest`. Read out of this server's own request log across three attempts, it asks:

```
GET  /admin/generateToken?f=json
GET  /admin/rest/info?f=json
GET  /services/rest/info?f=json
POST /services                     (a SOAP body)
```

`/admin/generateToken` was built on 2026-08-20 and now answers. Pro accepts it and moves straight to
`POST /services` — six times, then it stops. **So exactly one thing stands between a Pro user and a
browsable connection, and it is a SOAP service catalogue.**

## 2. The question

Not *whether* to serve Pro users — the owner has decided that. **Which of two roads.**

## 3. Alternatives

### Alternative A — the SOAP service catalogue, at `POST /services`

**Argument for.** It is precisely and only what Pro's *New ArcGIS Server* connection is missing. The
probe before it already passes.

**Argument against.** It is not an endpoint; it is a protocol family — a WSDL, a per-service SOAP
binding and its own type system, all of it the shape of a product that predates the REST API by
years. [ADR-005](ADR-005-api-architecture.md) never scoped it and
[protocol-surface.md](../protocol-surface.md) does not list it among the twenty-nine faces. It is
also the surface with the least public specification of anything this project has adopted, which
makes [CLAUDE.md](../../CLAUDE.md) §5's clean room expensive here in a way it has not been elsewhere:
the ArcGIS REST API is published, and the SOAP catalogue is substantially not.

**And it buys one affordance.** Everything a Pro user does after connecting already works.

### Alternative B — the portal surface, at `/sharing/rest` (chosen)

Pro's other connection type is **New Portal**, and it speaks the ArcGIS REST API rather than SOAP:
`sharing/rest` for discovery, `generateToken` for authentication, `portals/self` for the
organisation, `community/self` for the signed-in user, `search` and `content/items` for what is
published.

**Argument for.**

- **The model is already ours.** [ADR-019](ADR-019-portal-server-split.md) §1 maps Portal's tier —
  items, users, groups, roles, sharing — onto `Graticula.Platform`, and says so in a table written
  before any of this came up. This surface publishes concepts that exist rather than inventing them.
- **It is REST and JSON**, which is what every other surface here is made of, and it is publicly
  documented, so §5 costs nothing.
- **It is the road Esri's own users are on.** A portal connection is how ArcGIS Online and ArcGIS
  Enterprise are reached; a server connection is how the older product is reached.
- **The peer took the same road.** Their published README lists `/sharing/rest/generateToken` and
  no SOAP surface at all.

**Argument against, and it is the honest one.** *That Pro's portal connection succeeds against a
portal Esri did not write is not measured.* Three predictions were made about Pro on 2026-08-19 and
2026-08-20; two held and one did not, and the one that did not was about exactly this kind of
client-side discovery. This decision is taken knowing that.

## 4. Decision

**Build the minimum portal surface at `/sharing/rest`, and measure Pro against it before building
anything more.**

- `sharing/rest` and `sharing/rest/info` — version and how to authenticate.
- `generateToken` — the same `LoginService` as every other door, so the throttle and the audit
  record stay shared. This is the third spelling of one operation and the third is where a copy
  usually appears; it does not.
- `portals/self`, and the same document under `portals/{id}` and `accounts/{id}` — a client asks
  for the organisation by both of its names and finds half a portal if only one answers.
- `community/self` and `community/users/{username}` — the signed-in user. Only the caller's own
  profile: this server has no member directory, and inventing one to fill a field would publish the
  user list through a door nobody reviewed.
- `search`, on GET and POST — published services as items. Pro's query runs to a paragraph and it
  uses a body.
- `content/items/{id}`, `content/items/{id}/data` and `content/users/{username}`.
- `portals/{id}/subscriptionInfo`, `portals/{id}/categorySchema` and `community/groups` — three
  documents an organisation has and this one does not. Each answers with an empty truth rather than
  a 404, because a client asks repeatedly and reads absence as a broken portal.
- `/arcgisuris.xml` at the root, outside `/sharing`, because a client handed a bare host has no
  other way to learn where the portal lives.

**Every route answers HEAD as well as GET**, which is not a detail: Pro sends HEAD before each
probe and reads 405 as a dead end.

**The search reads its query.** A clause this server cannot evaluate returns nothing rather than
everything, because Pro looks for its geocoder by URL and *everything* would have answered that a
provinces layer is a geocoding service.

**An item is a published service, not a new thing to keep.** Its identity is the service's own id,
its `url` is the FeatureServer or VectorTileServer address that already works, and its `access` is
the sharing scope [ADR-018](ADR-018-authorization-and-roles.md) already decides. Nothing is stored
for the portal surface and nothing can drift out of step with the catalogue, because there is no
second copy.

**The SOAP catalogue is not built.** If Pro's portal connection does not arrive, that is a new
decision with this ADR's evidence in front of it, not a fallback to be taken quietly.

## 5. Consequences

**Positive.** A Pro user gets the browse workflow the owner asked for. Every other ArcGIS client
that speaks the portal API — ArcGIS Online tooling, the Python API, the JS API's portal helpers —
arrives at the same door. And it makes ADR-019's mapping load-bearing rather than descriptive.

**Negative.** A third token endpoint, on a server that had none four days ago. A surface whose
vocabulary is somebody else's, which §51 keeps out of the core and which still has to be maintained
against their changes. And **v2 grows a second item while v1's carried debts are open** — the same
cost [ADR-039](ADR-039-wfs-is-the-first-surface-after-v1.md) §1 recorded, taken a second time, and
recorded rather than absorbed.

**Ports created.** None.

## 6. Conditions

1. **DISCHARGED 2026-08-20. ArcGIS Pro connects, browses and adds.** It signs in as `root`, lists
   all eleven items under *My Content*, and adding one to a map produces the query traffic the
   ArcGIS surface already answered: `returnCountOnly`, `outFields=objectid`, then `outFields=*`
   with a where clause, all 200.

   **It took seven rounds and every one of them was read out of the request log rather than
   guessed.** The chain, in the order it broke: `HEAD` answered 405 where `GET` answered 200 and Pro
   leads with `HEAD`; `/arcgisuris.xml` was absent, and then present with `<Name>Graticula</Name>`,
   which told Pro this was ArcGIS Online and sent it to arcgis.com for OAuth it would never get;
   `portals/self` lacked the fields a client classifies on, so the reference was read off a working
   Enterprise portal instead of guessed at again; `community/users/{username}` was missing, so a
   sign-in that had already succeeded reported as a failed connection; `accounts/{id}` was missing;
   `POST` on search was missing, because Pro's query is a paragraph and it uses a body; and
   `content/items/{id}/data` was missing, which stopped the add after the FeatureServer document had
   already been read.

   **The one that was nearly a wrong answer rather than a missing one:** search ignored `q`, and Pro
   looks for its geocoder with `q=url:https://geocode.arcgis.com/…/GeocodeServer`. Returning
   everything would have told Pro that a provinces layer is a geocoding service. The query is read
   now, and a clause this server cannot evaluate returns nothing rather than everything — the same
   rule `FilterReader` follows for WFS, arrived at from the other direction.
2. **The item list is the catalogue's, filtered by the same sharing evaluation as everything
   else** — asserted by a test that compares an anonymous caller's items against an authenticated
   one's, the way `/admin/routes` is asserted rather than assumed.
3. **The third token endpoint shares the second's implementation**, not its shape. A test signs in
   through all three and asserts the same session store answers.
   *(Discharged 2026-08-27 — `PortalConformanceTests.All_three_token_endpoints_answer_from_one_session_store`
   posts credentials to `/rest/generateToken`, `/admin/generateToken` and
   `/sharing/rest/generateToken` in turn, and then **spends each token on a surface none of the
   three belongs to** — which is the part that makes it about one session store rather than
   three that happen to answer. Passing. **It was met and marked live**, found by sweeping this
   ADR's conditions against the tests.)*
4. **If Pro still does not connect, the finding is written into [Q-126](../open-questions.md) with
   the log**, and Alternative A is reconsidered as a decision rather than resumed as a reflex.

## 7. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-075 | ArcGIS Pro's portal connection works against a portal API this project implements, rather than requiring something only Esri's own portal returns | **`VALIDATED` 2026-08-20** — Pro connected, browsed and added a layer. **Nothing Esri-only was required**; what was required was ten endpoints and one classification token, and the whole of it is JSON this project already knew how to write. **The argument against in §3 was right about the risk and wrong about the outcome**, and the reason it was survivable is that each failure was a line in the request log rather than a message in a dialog: Pro says *unable to connect* for all seven distinct causes |

## 8. Revisit triggers

- **Condition 1 fails.** Then the browse workflow costs SOAP after all, and the owner decides again
  with a real number in front of them.
- **Somebody asks to publish *into* this server from Pro.** The portal API has a write half — item
  upload, sharing changes — and this ADR builds none of it.
- **The portal surface starts holding state of its own.** The moment an item is anything but a view
  of a published service, this decision's main argument has gone.

## 9. Dissent

**The affordance is browsing, and the product already works without it.** A Pro user can use every
capability this server has by pasting a URL. What is being bought here is discovery, and it is being
bought with a whole protocol surface — the second one in two days, on a product whose §66 gates
still stand at FAIL. The counter-argument is the owner's and it is not weak: a workflow nobody can
find is a workflow nobody uses, and *paste this URL* is not how a GIS analyst has ever been taught
to add data. But the cost is real and it is recorded here rather than discovered in the next gate.
