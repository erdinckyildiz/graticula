# ADR-018 — Authorization: user types, roles and privileges

| | |
|---|---|
| **Status** | `REOPENED` 2026-08-14 by owner direction → **re-decided**, `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-08-13 · **re-decided 2026-08-14** |
| **Answers** | **Q-59** · unblocks [ADR-017](ADR-017-admin-api.md), debt **D-09** · closes **D-11** |

---

## 0. Why this was reopened, one day after it was accepted

The first version invented four roles over nine permissions, marked the set
`INFERRED`, and made owner confirmation condition 1. **Condition 1 did its job.**

> **Owner direction, 2026-08-14:** *use the role / user-type capability matrix
> defined in ArcGIS Portal.*

That is not a confirmation of the four roles, and it is not a small edit either.
It replaces a model we invented with one that already exists and is publicly
documented — and it drags in one structural consequence that the first version
had explicitly deferred.

*(Written here on 2026-08-13 as "one our target users already know". The
owner's market statement the next day made that false for most of them; see
§1.)* §2
explains that consequence, because it is the whole reason this is a rewrite
rather than a rename.

**This is now an owner decision, not an inference.** What remains inferred is
narrower and is marked as such in §5.

---

## 1. Why adopting it is right, beyond being asked

Three independent reasons, and the first is the weakest:

- **Recognition.** Q-06a made the GIS administrator our primary user. They
  already hold a mental model of Viewer / Data Editor / Publisher /
  Administrator, and a product that invents four different names makes them
  translate on every screen.
  **Struck through on 2026-08-14 and reinstated the same day.** It was struck on
  a misreading of the owner's market statement — *cannot pay for ArcGIS
  licences* was taken to mean *has never used ArcGIS*. The licence in question
  is ArcGIS **Enterprise**, not ArcGIS Pro, so the target is an organisation
  that already works in ArcGIS and cannot afford the server. They know these
  role names. **This is the strongest of the three reasons, not the weakest.**
- **Migration (Q-16) becomes possible rather than approximate.** Importing an
  existing deployment's role model is only meaningful if the target has
  somewhere to put it. Four invented roles would force every import to be a
  lossy mapping, and lossy in the *widening* direction — see §4.
- **It is a better model than ours was.** §2 — ours could not express *two
  layers with two audiences*, which is the second thing anybody does. This one
  holds regardless of who the user is, which is why it survived the misreading
  above intact.

**Clean room (CLAUDE.md §5) permits this and constrains how.** ArcGIS Portal's
privilege and role model is publicly documented behaviour, which is precisely
the category §5 allows. What we must not do is copy Esri's documentation text or
treat their privilege catalogue as ours to publish. §6 puts the compatibility
vocabulary where §51 says it belongs — outside the core domain.

---

## 2. The consequence that makes this a rewrite: reading is not a privilege

**ArcGIS Portal has no read privilege.** Look for one and it is absent: roles
grant *doing* — create content, publish, edit features, administer. Whether you
can *see* an item comes from the **sharing** axis: the item is private to its
owner, shared with a group, shared with the organisation, or public.

Our first version had `layer.read` as a privilege carried by a `viewer` role,
and made a public portal work by granting that role to the anonymous principal.
That is not how the model we have been told to adopt behaves, and the difference
is not cosmetic:

| | First version | Portal model |
|---|---|---|
| Who can read layer X? | anyone holding `viewer` | anyone X is *shared with* |
| Two layers, different audiences | impossible | ordinary |
| Public portal | grant `viewer` to `anonymous` | mark the layer public |
| Private draft | impossible | the default |

The first version could only make the **whole server** readable or not.
[security.md](../security.md) §2.0 already said authorization has two axes and
that the second was owner-set; ADR-018 §6 then deferred it as *not due until the
publish endpoint ships*. **That deferral is now wrong**, because in this model
the sharing axis is not an enhancement to reading — it *is* reading.

So it is built now, in its minimal form (§3b).

---

## 3. Decision — two axes, exactly as Portal has them

### 3a. Effective capability is the intersection

```text
what you may do  =  what your user type allows  ∩  what your role grants
```

- A **role** is a named set of privileges, assigned by an administrator.
- A **user type** is a *ceiling*: the set of privileges a role is permitted to
  confer on this member.

**We have no licences to meter, and we are adopting the ceiling anyway.** That
needs justifying against CLAUDE.md §6 — *what concrete problem does this solve?*
— because a licensing mechanism in a product with no licences is exactly what
§82 exists to catch.

The answer is Q-16. When migration imports a Portal deployment where a member
holds the Publisher **role** but a Viewer **user type**, the original system
gave them viewing only. An import that keeps the role and drops the ceiling
grants publishing rights the source system withheld. **Silent privilege
escalation during migration is the worst possible import bug**, because the
administrator has no reason to re-audit a system they believe they copied.

So the ceiling is enforced, and it costs nothing in a fresh install: the default
user type, `unrestricted`, contains every privilege. Nothing is withheld unless
somebody deliberately assigns a narrower type.

**2026-08-14, after a correction.** This was briefly recorded as narrowed, on
the reading that most of the market had never used ArcGIS and would never import
anything. With the licence clarified as ArcGIS **Enterprise**, the opposite
holds: the target arrives *with* an existing deployment to bring across, so
Q-16 import safety is a mainstream concern rather than an edge one, and this
justification is stronger than when it was written.

### 3b. Sharing governs reading

Every layer carries an owner and a sharing scope:

| Scope | Who may read it |
|---|---|
| `private` | the owner, plus any principal with the *view all content* administrative privilege |
| `organization` | any **authenticated** principal |
| `public` | anyone, including anonymous |

**The default is `private`**, which preserves the closed default the first
version had (§3 of the superseded text) while reaching it the way Portal does.
The behaviour change from every build before 2026-08-13 stands: a fresh server
publishes nothing to the unauthenticated, and that is now a property of each
layer rather than of the whole server.

**Groups are deliberately absent.** Portal's fourth scope is *shared with a
group*, and groups are a real object with membership, ownership and their own
privileges. They are not needed to make reading work, and adding them here would
be adopting a subsystem to complete a table. Deferred, and the scope column
takes a string so adding `group` later is a value rather than a migration.

### 3b-ii. Who may change what — the split the owner likes, without the split that causes it

*Added 2026-08-17, after the owner described the ArcGIS Enterprise arrangement they
want: on the Server side an administrator may start, stop, delete and tune a service —
protocols and limits — and nothing else; on the Portal side the owner of the published
item decides who may read it.*

**That separation is worth having and it is not a separation of stores.** In ArcGIS it
looks like one because the two halves live in two products, and the seam is visible in a
way the owner called *saçma*: deleting a service in Server leaves its Portal item behind,
pointing at nothing. That is not a design choice anybody made. It is what two catalogues
of the same fact produce when one is edited.

**We do not inherit it, and this is the clearest dividend of
[ADR-019](ADR-019-portal-server-split.md) so far.** A service and its item are one row
here. There is no second record to strand, so *delete leaves a dangling item* is not a bug
we can have — and the fusion that made the datastore mandatory and looked like a cost
(§4's spent isolation) is what buys it.

**So what carries over is the audience separation, expressed as privileges on one
object:**

| Act | Who | Where it already lives |
|---|---|---|
| Start, stop | Administrator | ADR-020 §3, `admin:manageServer` |
| Protocols, limits, timeout | Administrator | [ADR-031](ADR-031-service-capability-configuration.md), `admin:manageServer` |
| Delete the service | Administrator **or its owner** | `content:create` owns it; ADR-018 §4's administrative privileges |
| Who may read it | Its **owner** | §3b's sharing scope, on the item |

**The rule that makes it coherent: tuning is not access.** An administrator may make a
service serve less and may not make it visible to more people; an owner may change who
reads it and may not change what it costs the server. Neither can do the other's job,
which is why ADR-031 made configuration a ceiling that only narrows and kept sharing out
of it (§2b) — the same wall, seen from the other side.

**Groups are still absent and are now a named question.** The owner wants a private layer
shareable with a group; [Q-112](../open-questions.md) records the decision with ArcGIS's
semantics confirmed from Esri's documentation rather than assumed, including two
constraints worth knowing before copying: the update capability is fixed when a group is
created and cannot be changed afterwards, and *update* excludes deleting, re-sharing and
changing ownership even inside a group that grants it.

### 3b-i. A service that is not a layer is still a service

**Added 2026-08-15, after the project owner said: *"geometry server is also a
service. we might make all services public, private or organization."* They were
correcting an omission, and the omission had teeth.**

§3b above says *every **layer** carries an owner and a sharing scope*, and that
is exactly what was built. The geometry service has no layer. So it was governed
by nothing at all: `POST /rest/services/Utilities/Geometry/GeometryServer/project`
answered `200` to an anonymous caller, on a server whose stated default is that a
fresh install publishes nothing to the unauthenticated.

**That was not a decision anybody took.** It is what happens when an
authorization model is built around *content* and something ships that is not
content. Nothing in the code said "the geometry service is public"; there was
simply no place for it to be anything. A gap of that shape does not appear in a
review of the sharing code, because the sharing code is correct — it appears
only when somebody asks what governs the thing that is not on the list.

The correction:

- A system service — a service with no layer behind it — carries the **same
  three scopes** as a layer. One concept, not two.
- It has **no owner**, so `private` means *administrators only*, reached through
  `admin:viewAllContent` rather than through ownership.
- The seeded scope for the geometry service is **`organization`**, not `public`.
  Closed defaults are §3b's rule and the reason for it does not weaken here; the
  service costs CPU and, per [ADR-022](ADR-022-geometry-server.md), is the
  surface whose adversarial cost measurement invalidated A-042. Anonymous access
  to it is a decision an administrator can take, not one we take for them.
- Changing it is `PUT /admin/services/{name}/sharing`, taking the **same
  privileges as a layer's sharing** — `sharing:shareToPublic` to open it to the
  world. Opening the geometry service to anonymous callers is the same act as
  opening a layer to them, and a separate privilege would let one be granted
  without the other.
- Enforcement is a **route-group filter**, not a call in each handler. Five
  handlers each remembering a guard is five chances to forget, and forgetting is
  precisely how this gap started.

**A second omission of the same shape, found 2026-08-17 and closed the same day.** The owner,
looking at the geometry service's row in the console: *"geometry server'in, startı stop'u,
timeout'u vs si yok mu?"* — hasn't it got a start, a stop, a timeout? **It had no status at all.**
This section gave a system service the three sharing scopes and stopped there, so `system_service`
carried sharing and nothing else, and *whether it answers* was a question with no place to be
asked. It is the identical failure to the one above — a model built for one axis, and something
arriving that needs two — which is why it is recorded here rather than as a separate decision.

**And the console was already showing a status.** The row drew a `started` pill that was a literal
in the markup: a client asserting a server fact nobody had asserted. That is the more instructive
half. A missing field is visible to whoever looks for it; a *fabricated* one is invisible, because
the screen looks complete.

The correction, migration 19:

- A system service carries a **started/stopped status**, the same two values a layer's service
  carries, set by `POST /admin/services/{name}/{start,stop}` under `admin:manageServer`. Stopping
  is an operational act about capacity, not a content act about audience — the same split §3b-ii
  draws for layers.
- **Sharing and status move independently**, asserted in both directions by test, because the
  endpoint's own answer promises that *starting it restores exactly the audience it had* and that
  sentence is only true if the two setters touch different columns.
- A stopped service answers **503, and the sharing refusal stays 404.** Sharing is checked first:
  telling a caller outside the audience that a service is *stopped* would confirm it exists, so
  only somebody allowed to use it learns that it is off. Within the audience, 503 rather than 404
  because *turned off* and *not there* are different facts and a client's log full of 404s sends
  an operator to check the URL.
- **Started is the seeded default**, since the service has answered since it shipped and a
  migration that left it stopped would remove a working endpoint as a side effect of adding a
  column.

**What this does not fix.** Nothing structural prevents the next service without
a layer from arriving with the same hole. The check that would — every mapped
route under `/rest/services` is either governed by a layer's sharing or by a
`system_service` row — is not written. Recorded as **D-28**.

### 3c. The default roles are Portal's

| Role | Portal's meaning, as we implement it |
|---|---|
| `viewer` | Read what is shared with them. No privileges at all — see §3b |
| `data_editor` | Viewer, plus edit features in layers shared with them |
| `user` | Viewer, plus create and own content |
| `publisher` | User, plus publish hosted layers, register data sources, publish over them |
| `administrator` | Everything, including members, roles, all content, and the server |

**`viewer` carrying no privileges is the model working, not a mistake.** A viewer
can read plenty; reading is simply not a privilege. Anyone who finds that
surprising is holding our first version's model, which is the one being replaced.

**Custom roles** are what Portal has and we do not yet: a role is a set of
privileges, so a custom one is a row plus a privilege list. The schema already
allows arbitrary role names. Deferred, and no longer for the reason the first
version gave — the privilege catalogue is no longer ours to invent, so freezing
it is no longer the risk it was.

---

## 4. Decision — the privilege set

Grouped as Portal groups them. **Only the privileges that mean something for a
GIS server appear**; a portal has websites, groups, story maps and premium
credit-metered services, and inventing local equivalents so the table looks
complete would be the opposite of §6.

### General — content

| Privilege | Carried by |
|---|---|
| `content:create` — create and own items | `user`, `publisher`, `administrator` |
| `content:publishFeatures` — publish a hosted feature layer | `publisher`, `administrator` |
| `content:publishTiles` — publish a hosted tile layer | `publisher`, `administrator` |
| `content:registerDataStore` — register a data source | `publisher`, `administrator` |
| `features:edit` — edit features in layers shared with you | `data_editor` and above |
| `features:fullEdit` — edit and delete regardless of editor tracking | `publisher`, `administrator` |

### General — sharing

| Privilege | Carried by |
|---|---|
| `sharing:shareToOrganization` | `user` and above |
| `sharing:shareToPublic` | `publisher`, `administrator` |

**Sharing to public is separated from sharing to the organisation**, as Portal
separates them, and it is the single most consequential privilege in the table:
it is the one that puts data on the internet. A deployment that wants
publishing without public exposure withholds exactly this.

### Administrative

| Privilege | Carried by |
|---|---|
| `admin:manageMembers` — create, disable, reassign principals | `administrator` |
| `admin:manageRoles` — grant and revoke roles and user types | `administrator` |
| `admin:viewAllContent` — see items regardless of sharing | `administrator` |
| `admin:manageAllContent` — update, delete and reassign any item | `administrator` |
| `admin:manageSecurity` — certificates, sessions, authentication settings | `administrator` |
| `admin:manageServer` — migrations, pools, workers, pinning | `administrator` |

**`registerDataStore` sits under content and not under administration**, which
is Portal's placement and also ours from the first version — for the reason
recorded there: registering hands the server a credential to somebody else's
database, and every layer over it inherits that reach. Portal puts it in the
publisher's hands; so do we, and the risk note stays.

### Premium

Portal's premium tier meters credit-consuming services — geocoding, network
analysis, GeoEnrichment. **We have none, and GeometryServer is not one.** The
group is named here only so that its absence is a decision rather than an
oversight.

---

## 5. What is still inferred

The owner named the model. These are ours and are marked `INFERRED`:

1. **Enforcing the user-type ceiling** rather than recording it for display
   (§3a). Justified by Q-16 import safety.
2. **Which Portal privileges have no local equivalent** (§4) — groups,
   websites, premium.
3. **Three sharing scopes rather than four** (§3b), groups deferred.

**Condition 2 covers the part that must not be guessed**: the exact privilege
identifiers and default-role assignments must be checked against Esri's
published documentation before any surface claims Portal compatibility. The
table above is the shape, verified structurally and not line by line.

---

## 6. Where the compatibility vocabulary lives

CLAUDE.md §51: *any compatibility adapter stays outside the core domain.*

- **Tier 1 holds our own privilege enum.** The names in §4 are ours, chosen to
  match Portal's shape closely enough to be recognisable.
- **The mapping to Portal's wire identifiers** — `portal:user:createItem` and
  the rest — belongs in the ArcGIS compatibility layer, next to the rest of the
  ArcGIS surface, and is written when an endpoint needs to speak them.

Doing it the other way — Esri's identifiers as our internal enum — would put a
third party's vocabulary in the middle of the domain and make every future
divergence a breaking change to our core.

---

## 7. Consequences

- **The sharing axis is built now, not deferred.** This is the largest change
  from the superseded version and the reason it is a rewrite.
- **`layer.read` ceases to exist as a privilege.** Read is computed from owner,
  scope and the caller.
- **The layer table gains an owner and a scope.** An expand migration; nothing
  existing is narrowed, and unowned pre-existing layers default to `private`,
  which is the safe direction and will look like a regression to exactly one
  person — see §8, condition 4.
- **A user-type table and a per-principal assignment.** Default `unrestricted`.
- **D-11 closes.** D-09 stays open but unblocked.
- **ADR-017's endpoints have privileges to require**, and now also a sharing
  scope to set.
- **security.md §2.0's open question — how an owner-set scope and an admin-set
  role compose when they disagree — is answered** for the read case: the scope
  decides who may read, and `admin:viewAllContent` is the documented override.
  It is *not* answered for write.

## 8. Conditions

1. **The ceiling is tested for the escalation it exists to prevent** — a
   principal with a narrow user type and a wide role gets the intersection, and
   the test is written from the migration-import scenario, not from the unit.
2. **The privilege identifiers and default-role assignments are verified against
   Esri's published documentation** before any surface claims Portal
   compatibility. §5 records that the current table is structural.
3. **`admin:viewAllContent` is auditable.** An administrator reading a private
   layer is legitimate and must leave a record, or the sharing model is
   decorative.
4. **The upgrade is walked on a store that already has layers**, and the
   operator is told that existing layers became private. Silently privatising
   somebody's published data is a worse regression than the closed default was.
5. ~~**No route under `/rest/services` is reachable without a sharing decision
   behind it.**~~ **DISCHARGED 2026-08-15.** Every such route carries a
   `SharingGoverned` marker naming what governs it; `GET /admin/routes` lists
   them with an `ungoverned` count; a conformance test asserts that count is
   zero, and was verified by adding an unmarked route and watching it fail.
   **The marker is not the enforcement** — `ServiceLookup` and the geometry
   group's filter are — it is what makes the absence of enforcement visible,
   which is precisely what was missing when the geometry service shipped
   answering anonymously.

## 9. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-048 | Portal's role model maps onto a GIS server without a portal's group and item machinery | `UNVALIDATED` — §4 drops three privilege groups as inapplicable. If groups turn out to be load-bearing for sharing, §3b's three scopes are not enough |
| A-052 | Enforcing the user-type ceiling costs nothing in deployments that never use it | `UNVALIDATED` — true by construction while `unrestricted` is the default, but it is a second lookup and a second place a grant can be silently withheld. The confusing failure is "I granted publisher and they still cannot publish" |

## 10. Dissent

**Against adopting the ceiling (§3a).** It is a licensing mechanism in a product
with no licences, and Q-59 explicitly guessed we would need only roles. The
counter is Q-16: without it, every imported deployment is widened, silently.
Recorded because if migration import is ever descoped, this should be revisited
rather than inherited.

**Against dropping `layer.read`.** It was simple, and per-layer sharing is more
machinery than a server that currently publishes two demo layers needs. The
counter is that the machinery is what was asked for, and that the simple version
could not express *two layers with different audiences* — which is not an
advanced requirement, it is the second thing anybody does.

---

## Superseded

The 2026-08-13 version — four invented roles (`viewer`, `publisher`,
`gis-administrator`, `platform-administrator`) over nine permissions, with
`layer.read` and a closed default reached by granting no roles to anonymous — is
superseded in full. Its reasoning about separating `datasource.register` from
hosted publishing survives into §4 and is credited there.
