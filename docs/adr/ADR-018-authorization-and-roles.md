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
It replaces a model we invented with one that already exists, is publicly
documented, and that our target users already know — and it drags in one
structural consequence that the first version had explicitly deferred. §2
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
- **Migration (Q-16) becomes possible rather than approximate.** Importing an
  existing deployment's role model is only meaningful if the target has
  somewhere to put it. Four invented roles would force every import to be a
  lossy mapping, and lossy in the *widening* direction — see §4.
- **It is a better model than ours was.** §2.

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
