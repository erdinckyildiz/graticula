# ADR-018 — Authorization: the role set and what each carries

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-08-13 |
| **Answers** | **Q-59** · unblocks **Q-92**, [ADR-017](ADR-017-admin-api.md), debt **D-09** and **D-11** |

> **⚠ The role set below is `INFERRED`, not stated by the project owner.**
> [CLAUDE.md](../../CLAUDE.md) §2 requires that distinction to be explicit.
> It is derived from Q-59's own wording — *"At minimum: viewer, publisher (may
> create hosted content), GIS administrator (may register sources and publish
> from them), platform administrator"* — which is the owner's enumeration rather
> than ours. **Condition 1 is owner confirmation.** It is cheap to change now and
> expensive once grants exist in deployed stores.

---

## 1. Context

Authentication is implemented ([ADR-015](ADR-015-authentication.md)) and
resolves a principal on every request. **Nothing consults it**, because there
are no roles to consult: the `role` table ships empty by deliberate decision,
recorded as debt **D-11**.

That is not a gap more authentication can close, and it blocks more than itself.
The admin API (ADR-017) is the largest remaining friction in the product —
**D-09** has publishing a layer going through hand-written SQL and a connection
string sealed outside the server — and publish endpoints without authorization
would be strictly worse than the SQL they replace.

[security.md](../security.md) §2.0 already established that authorization has
**two axes**:

| Axis | Question | Assigned by |
|---|---|---|
| **Role** | What may this person *do*? | An administrator |
| **Ownership and sharing** | Who may see *this item*? | The item's creator |

**This ADR decides the role axis only.** The ownership axis is not designed
here, and §6 says why that is honest rather than convenient.

---

## 2. Decision — four roles, fixed in v1

Q-59 asked *"are roles fixed or custom?"* — **fixed**, and custom roles are
deferred rather than refused.

The reason is that a custom role is an expression over a **permission
catalogue**, and the moment customers write roles against that catalogue it
becomes a public contract that cannot be renamed or split without breaking their
grants. We have implemented three endpoints. Freezing a permission vocabulary
now would be guessing at the shape of a surface that does not exist.

The `role` and `principal_role` tables already carry arbitrary names, so custom
roles arrive as a feature rather than a migration.

| Role | Carries | Intended holder |
|---|---|---|
| `viewer` | Read published layers | Anyone who consumes maps |
| `publisher` | `viewer`, plus create and own hosted content | A GIS analyst publishing their own data |
| `gis-administrator` | `publisher`, plus register data sources, publish over them, and override sharing | Q-06a's **primary user** |
| `platform-administrator` | Everything, plus principals, roles, sessions and server operations | Whoever runs the server |

**They nest, deliberately.** A non-nesting matrix is more expressive and, at
four roles, expressive about nothing — while making "why can the administrator
not read this layer?" a reachable state. Nesting is a claim we can drop later
without breaking a grant; adding it later would silently widen every existing
one.

### 2a. Permissions

Roles are named bundles; the checks are against permissions.

| Permission | `viewer` | `publisher` | `gis-administrator` | `platform-administrator` |
|---|:--:|:--:|:--:|:--:|
| `layer.read` | ✓ | ✓ | ✓ | ✓ |
| `layer.publish.hosted` | | ✓ | ✓ | ✓ |
| `datasource.register` | | | ✓ | ✓ |
| `layer.publish.registered` | | | ✓ | ✓ |
| `sharing.override` | | | ✓ | ✓ |
| `principal.manage` | | | | ✓ |
| `role.grant` | | | | ✓ |
| `session.manage` | | | | ✓ |
| `server.operate` | | | | ✓ |

**`datasource.register` is separated from `layer.publish.hosted`** because they
are different risks wearing the same word. Publishing hosted data puts a file in
our datastore. Registering a data source hands the server a **credential to
somebody else's database**, and every layer published over it inherits that
reach. A publisher who can upload a shapefile should not thereby be able to make
the server connect anywhere.

**`server.operate` covers migrations and certificates**, which are ADR-016 and
ADR-014 operations. It is `platform-administrator` only because a schema
migration can close the rollback window (ADR-016 §4a) and that is not a GIS
decision.

### 2b. What is *not* a permission

**Publishing executable code.** ADR-015 §7 and security.md §362 both require the
grant for publishing a Python geoprocessing tool to be **separate from the
publisher role** — a publisher uploading a shapefile and a publisher uploading
code that runs on our server are not the same risk.

It is absent from the table because Q-88 cut user-supplied Python from v1
entirely. **It is absent, not folded in**, and this paragraph exists so that
whoever adds GPServer finds the requirement rather than the convenient
assumption that `publisher` already covers it.

---

## 3. Decision — anonymous holds no roles by default

ADR-015 §2a made anonymous a real principal so that granting it access would be
configuration rather than a special case. This is that promise coming due, and
the default goes the other way.

**A fresh server publishes nothing to the unauthenticated.** Making a portal
public is one grant — `viewer` to `anonymous` — and it is a deliberate act by
someone who has authenticated to perform it.

**This is a behaviour change and it will look like a regression.** Until now
every published layer was world-readable. Open data portals are a normal
deployment of a GIS server and this makes them one command harder to stand up.
Accepted anyway: the failure modes are not symmetric. A public portal that needs
a grant is discovered in a minute by the person setting it up. A private dataset
that was public by default is discovered by someone else.

---

## 4. Decision — the first administrator is created with the role

The setup token (ADR-015 §6) creates a principal. It now also grants
`platform-administrator`, **in the same transaction**.

Obvious, and stated because the alternative is reachable by accident: a setup
flow that creates an account and no grant produces a server with exactly one
account, which can do nothing, and no way to grant anything to it. The recovery
is hand-written SQL, which is the thing this whole line of work exists to remove.

---

## 5. Decision — 401 and 403 mean different things

- **401** — you are anonymous and this needs a principal. *Authenticate.*
- **403** — you are authenticated and lack the permission. *Ask an administrator.*

Collapsing them to 404 hides existence, which is worth doing for a resource
whose *name* is sensitive. Layer names are already listed by the catalogue
endpoint to anyone permitted to see it, so hiding them here would buy nothing and
cost every operator the ability to tell "wrong credential" from "wrong grant".

**Where a name genuinely is sensitive, that is the ownership axis's problem**
(§6), not this one.

---

## 6. What this deliberately does not decide

**The ownership and sharing axis.** security.md §2.0 flagged the hard part
already: *how an owner-set sharing scope and an admin-set role compose when they
disagree* — noting the safe default is that the more restrictive wins, and that
*"safe" and "expected" are not the same thing*.

Nothing here answers that, and nothing here needs to yet, because **v1 has no
self-service publishing**: layers are registered by an administrator. The axis
becomes live the moment ADR-017's publish endpoint ships, and security.md
already says it must be designed before publishing does.

**Group membership.** Sharing "to a group" needs groups. Deferred with the
ownership axis, for the same reason.

**Row-level and column-level restriction.** security.md §2's RLS delegation
(D-01) is per-row within a layer; everything here is per-layer. They compose —
this is the coarse gate — but the fine one is unbuilt.

---

## 7. Consequences

- **D-11 closes** when the checks are implemented; the role table stops shipping
  empty.
- **ADR-017 is unblocked.** Its endpoints have permissions to require.
- **A new migration**, taking the platform schema to 3. Expand-only: it inserts
  rows and grants nothing existing, so `minimum_reader_version` stays 1 and an
  older server can still read the store (ADR-016 §4a).
- **Every deployment gains a closed default** it did not have, per §3.
- **The permission vocabulary is now load-bearing** even though roles are fixed:
  the strings appear in refusal messages, so renaming one changes what operators
  read in logs.

## 8. Conditions

1. **Owner confirmation of the role set** (§2) and of the closed-by-default
   decision (§3). Both are `INFERRED`. Until confirmed, this ADR is a proposal
   that happens to be running.
2. **The ownership axis is designed before ADR-017's publish endpoint ships**,
   not alongside it. security.md §2.0 states the requirement; §6 states why it is
   not yet due.
3. **A refusal never reveals more than the caller may see.** D-03 already
   records that detailed refusals disclose topology; the 403 body must name the
   missing permission and nothing about the layer.
4. **The separate grant for publishing code exists before GPServer does** (§2b).

## 9. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-048 | Four nesting roles are sufficient until real deployments exist | `UNVALIDATED` — Q-49 dissolved the *test with real GIS teams* validation route, so the first evidence will be a complaint |
| A-049 | Fixing the role set does not block adoption | `UNVALIDATED` — ArcGIS and GeoServer both ship custom roles; a customer with an existing role model may find four insufficient |

## 10. Dissent

**Against fixing the role set.** Both products we intend to displace support
custom roles, and a migration tool (Q-16) that imports service definitions but
cannot import the role model imports half a deployment. The counter is that the
permission catalogue is nine entries derived from three endpoints, and freezing
it as a public contract now guarantees breaking it later.

**Against closed-by-default (§3).** A GIS server whose most visible use is
publishing open data should probably publish open data. The counter is in §3
itself: the two failure modes are not symmetric, and only one of them is found
by the person who caused it.
