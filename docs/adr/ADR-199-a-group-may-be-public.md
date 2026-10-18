# ADR-199 — A group may be public: anybody finds it, signed in or not

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-10-18, by owner decision — *"İkisini de yeniden aç"*, reopening Q-119 (*"public grup olmaz"*, 2026-08-25) after the ArcGIS veteran's review (V-94) |
| **Supersedes** | [Q-119](../open-questions.md)'s answer; [ADR-036](ADR-036-groups.md) §4b's two visibilities |
| **Superseded by** | — |

---

## 1. Context

Q-119 removed the *anybody* visibility because nothing honoured it: `/admin/groups` refuses a signed-out caller, so
`public` was enforced exactly as `organization` while the console promised more. The veteran's review (V-94) named
what that costs: an open-data portal's gallery and its basemap groups are public groups in ArcGIS, and a public app
here finds no group at all. The owner reopened it.

## 2. Decision

**`GroupVisibility.Public` is back**, stored as `public` (migration 91 widens the check migration 36 narrowed), and
**the place it happens is the portal's group search**: `sharing/rest/community/groups` answers a signed-out caller
with the public groups and no others. Each is answered as to any outsider — `access: "public"`, no member list, no
counts — and a group's content search answers a signed-out caller with what is shared with the public, as every item
search does. **Being found is still not being read**: the group grants a signed-out caller nothing.

**Making a group public takes `sharing:shareToPublic`**, the privilege an item's public sharing takes, as ArcGIS's
does. `/admin/groups` itself still needs a signed-in caller.

**The basemap gallery group is not given to a signed-out caller even when public** — Q-174 (*"giriş yapmamış
kullanıcı basemap seçemesin"*, 2026-10-15) is a decision of its own and was not reopened. **Confirmed 2026-10-18 by
the owner** — [Q-179](../open-questions.md), *"Gizli kalsın"*.

## 3. Counterarguments

Q-119's argument was that an unauthenticated reader is a new surface in a product that answers 404 rather than 403
so that nobody learns what exists. That still holds for everything but this: a public group is one its owner chose to
be found, with a privilege, and the listing shows its title, summary and owner — what ArcGIS shows.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| A signed-out caller finds a public group and not an organization group | `APublicGroupIsFoundByAnybodyTests` | conformance fixture |
| `public` is stored and read back | `GroupDirectoryTests.Public_is_a_visibility_again` | PostGIS |
| The portal word is ArcGIS's | `OurGroupsAreThePortalsGroupsTests` | this repository |

## 5. Consequences

**Positive.** An open-data portal can publish a gallery anonymous apps find.

**Negative.** A second anonymous read path, the first being the item search. The console offers two visibilities until
the redesign adds the third — the admin API takes all three ([D-302](../architecture-debt.md)).

**State.** `sharing_group.visibility` may hold `public` (migration 91), in the catalogue; nothing at runtime.

## 6. Conditions

1. **Q-179 is answered:** does a public gallery group reach a signed-out caller, which would undo Q-174? *(Discharged 2026-10-18: it stays hidden, by owner decision.)*
2. **The console offers *public*** — D-302, in the redesign.

## 7. Dependencies

**Depends on:** ADR-036, ADR-040 §4a, ADR-184 §5.4.

## 8. Revisit triggers

A signed-out caller observed reading a member list or a private item through a public group.

## 9. Dissent

Q-119's own recommendation, kept in its row: refuse rather than offer, until a discovery surface exists. It exists now.
