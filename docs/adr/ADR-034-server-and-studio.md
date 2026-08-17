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

### 5a. Two surfaces, one server

`#/server/…` and `#/studio/…`, in one application, over one API, with one stylesheet and one
map module. Not two deployables (ADR-019), not two identity stores
([ADR-015 §5a](ADR-015-authentication.md)), not two viewers.

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
| The anonymous view | Sharing |
| The administrative all-content listing | Data sources, and registering one |
| Audit | The map viewer and the query page |

A layer therefore appears in both, and that is correct: its *limits* are the server's business
and its *appearance* is the publisher's. Each surface links to the other's page for the same
layer, so the split never means a dead end.

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

## 6. Consequences

- **ADR-020 is amended.** Its §5e table of surfaces becomes Server's; Studio is new.
- **The console's URLs change**, and bookmarks to `#/services` and `#/layer/…` break. Said out
  loud because ADR-020 §5c took *frozen URLs* from the reference as a rule, and this breaks it
  once, deliberately, before there is anybody to break it for. The old roots redirect.
- **The anonymous view stays in Server**, which is right and slightly sad: it answers *what
  does a stranger see*, which a publisher would also like to know about their own layer. A
  per-layer version of that question belongs in Studio later.
- **Q-112 (groups)**, when answered, lands in Studio.

## 7. Conditions

1. **No screen appears that its reader cannot use**, asserted by a test that signs in without
   `admin:manageServer` and finds no Server surface — not a hidden one, not a 403 toast. The
   current console's per-section failure isolation makes the opposite failure invisible, which
   is why this is a test and not a review note.
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
